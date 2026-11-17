using System.Diagnostics;
using System.Runtime.InteropServices;
using NashmiKeys.Core.Input;
using NashmiKeys.Core.Logging;

namespace NashmiKeys.Core.Sdl;

/// <summary>
/// Dedicated SDL polling thread. Reads gamepad state every ~8 ms so hotkeys
/// work while the app is in the background, unions all connected pads, and
/// applies stick/trigger hysteresis before publishing a device-agnostic input
/// set. Never marshals SDL_Event.
/// </summary>
public sealed class GamepadPoller : IDisposable
{
    public const int PollIntervalMs = 8;
    private const long EnumerateIntervalMs = 250;
    private const long BatteryIntervalMs = 1000;
    private const double StickActivate = 0.75;
    private const double StickRelease = 0.65;

    private static readonly (int Button, string InputId)[] ButtonMap =
    {
        (Sdl3.GamepadButtonSouth, "south"),
        (Sdl3.GamepadButtonEast, "east"),
        (Sdl3.GamepadButtonWest, "west"),
        (Sdl3.GamepadButtonNorth, "north"),
        (Sdl3.GamepadButtonLeftShoulder, "l1"),
        (Sdl3.GamepadButtonRightShoulder, "r1"),
        (Sdl3.GamepadButtonLeftStick, "l3"),
        (Sdl3.GamepadButtonRightStick, "r3"),
        (Sdl3.GamepadButtonStart, "start"),
        (Sdl3.GamepadButtonBack, "back"),
        (Sdl3.GamepadButtonGuide, "guide"),
        (Sdl3.GamepadButtonTouchpad, "touchpad"),
        (Sdl3.GamepadButtonDpadUp, "dpad_up"),
        (Sdl3.GamepadButtonDpadDown, "dpad_down"),
        (Sdl3.GamepadButtonDpadLeft, "dpad_left"),
        (Sdl3.GamepadButtonDpadRight, "dpad_right"),
        (Sdl3.GamepadButtonMisc1, "misc1"),
        // Physical Xbox Elite numbering: P1/P2 are the right paddles, P3/P4 left.
        (Sdl3.GamepadButtonRightPaddle1, "paddle1"),
        (Sdl3.GamepadButtonRightPaddle2, "paddle2"),
        (Sdl3.GamepadButtonLeftPaddle1, "paddle3"),
        (Sdl3.GamepadButtonLeftPaddle2, "paddle4")
    };

    private readonly object _gate = new();
    private readonly ILogger _logger;
    private readonly Dictionary<uint, OpenPad> _pads = new();
    private readonly Dictionary<uint, FailedOpen> _failures = new();
    private readonly HashSet<uint> _detectedIds = new();
    private Thread? _thread;
    private volatile bool _stop;
    private bool _started;
    private volatile string? _lastError;
    private volatile string _sdlVersion = "not initialized";
    private long _lastEnumerationMs = -EnumerateIntervalMs;
    private bool _l2Active;
    private bool _r2Active;

    private readonly AxisHysteresis _leftStickLeft = new(StickActivate, StickRelease);
    private readonly AxisHysteresis _leftStickRight = new(StickActivate, StickRelease);
    private readonly AxisHysteresis _leftStickUp = new(StickActivate, StickRelease);
    private readonly AxisHysteresis _leftStickDown = new(StickActivate, StickRelease);
    private readonly AxisHysteresis _rightStickLeft = new(StickActivate, StickRelease);
    private readonly AxisHysteresis _rightStickRight = new(StickActivate, StickRelease);
    private readonly AxisHysteresis _rightStickUp = new(StickActivate, StickRelease);
    private readonly AxisHysteresis _rightStickDown = new(StickActivate, StickRelease);
    private double _l2Threshold = 0.5;
    private double _r2Threshold = 0.5;

    public GamepadPoller(ILogger? logger = null, double l2Threshold = 0.5, double r2Threshold = 0.5)
    {
        _logger = logger ?? NullLogger.Instance;
        UpdateThresholds(l2Threshold, r2Threshold);
    }

    public event Action<ControllerSnapshot>? SnapshotProduced;

    public string? LastError => _lastError;

    public string SdlVersion => _sdlVersion;

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _thread is { IsAlive: true };
            }
        }
    }

    public void UpdateThresholds(double l2Threshold, double r2Threshold)
    {
        _l2Threshold = Math.Clamp(l2Threshold, 0.05, 0.99);
        _r2Threshold = Math.Clamp(r2Threshold, 0.05, 0.99);
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_started)
            {
                return;
            }

            _started = true;
            _stop = false;
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "NashmiKeys.SDL"
            };
            _thread.Start();
        }
    }

    private void Run()
    {
        var initialized = false;
        try
        {
            _sdlVersion = SdlDiagnostics.GetVersionString();
            Sdl3.SDL_SetHint(Sdl3.HintJoystickAllowBackgroundEvents, "1");
            Sdl3.SDL_ClearError();

            if (Sdl3.SDL_Init(Sdl3.InitGamepad) == 0)
            {
                throw new SdlFailureException(
                    "SDL_Init(SDL_INIT_GAMEPAD) failed: " + Sdl3.GetErrorString());
            }

            initialized = true;
            Sdl3.SDL_SetGamepadEventsEnabled(0);
            Sdl3.SDL_SetJoystickEventsEnabled(0);
            TimeBeginPeriod(1);
            _logger.Info($"SDL initialized ({_sdlVersion}); polling every {PollIntervalMs} ms (events disabled).");

            var stopwatch = Stopwatch.StartNew();
            long nextTickMs = 0;

            while (!_stop)
            {
                try
                {
                    Sdl3.SDL_UpdateGamepads();

                    var now = stopwatch.ElapsedMilliseconds;
                    if (now - _lastEnumerationMs >= EnumerateIntervalMs)
                    {
                        EnumeratePads();
                        _lastEnumerationMs = now;
                    }

                    var snapshot = BuildSnapshot();
                    Publish(snapshot);
                }
                catch (Exception exception) when (exception is not DllNotFoundException)
                {
                    _logger.Error("Gamepad polling iteration failed.", exception);
                }

                nextTickMs += PollIntervalMs;
                var elapsed = stopwatch.ElapsedMilliseconds;
                if (nextTickMs < elapsed - 250)
                {
                    nextTickMs = elapsed;
                }

                var remaining = nextTickMs - stopwatch.ElapsedMilliseconds;
                if (remaining > 0)
                {
                    if (remaining > 1)
                    {
                        Thread.Sleep((int)(remaining - 1));
                    }

                    while (stopwatch.ElapsedMilliseconds < nextTickMs && !_stop)
                    {
                        Thread.SpinWait(200);
                    }
                }
            }
        }
        catch (DllNotFoundException)
        {
            SetFatalError("SDL3.dll was not found. Place SDL3.dll next to NashmiKeys.exe and restart the app.");
        }
        catch (BadImageFormatException)
        {
            SetFatalError("SDL3.dll has the wrong architecture. NashmiKeys needs the 64-bit SDL3.dll.");
        }
        catch (EntryPointNotFoundException exception)
        {
            SetFatalError("SDL3.dll is not a compatible SDL 3 build. " + exception.Message);
        }
        catch (Exception exception)
        {
            SetFatalError("Controller input could not start: " + exception.Message);
            _logger.Error("Gamepad poller stopped.", exception);
        }
        finally
        {
            CloseAllPads();
            if (initialized)
            {
                Sdl3.SDL_Quit();
            }

            if (initialized)
            {
                TimeEndPeriod(1);
            }

            _logger.Info("Gamepad poller stopped.");
        }
    }

    private void SetFatalError(string message)
    {
        _lastError = message;
        _logger.Error(message);
        Publish(ControllerSnapshot.Empty(_sdlVersion, message));
    }

    private void Publish(ControllerSnapshot snapshot)
    {
        try
        {
            SnapshotProduced?.Invoke(snapshot);
        }
        catch (Exception exception)
        {
            _logger.Error("Controller snapshot subscriber failed.", exception);
        }
    }

    private void EnumeratePads()
    {
        var pointer = Sdl3.SDL_GetGamepads(out var count);
        if (pointer == IntPtr.Zero && count > 0)
        {
            _logger.Warn("SDL_GetGamepads failed: " + Sdl3.GetErrorString());
            return;
        }

        var ids = new List<uint>(Math.Max(0, count));
        try
        {
            for (var index = 0; index < count; index++)
            {
                var id = (uint)Marshal.ReadInt32(pointer, index * sizeof(uint));
                if (id != 0)
                {
                    ids.Add(id);
                }
            }
        }
        finally
        {
            if (pointer != IntPtr.Zero)
            {
                Sdl3.SDL_free(pointer);
            }
        }

        _detectedIds.Clear();
        foreach (var id in ids)
        {
            _detectedIds.Add(id);
        }

        foreach (var id in ids)
        {
            if (_pads.ContainsKey(id))
            {
                _failures.Remove(id);
                continue;
            }

            if (_failures.TryGetValue(id, out var failure) &&
                Environment.TickCount64 < failure.NextRetryMs)
            {
                continue;
            }

            TryOpenPad(id);
        }

        var removed = _pads.Keys.Where(id => !_detectedIds.Contains(id)).ToList();
        foreach (var id in removed)
        {
            var pad = _pads[id];
            _pads.Remove(id);
            _logger.Info($"Controller disconnected: {pad.Name} (id {id}).");
            try
            {
                Sdl3.SDL_CloseGamepad(pad.Handle);
            }
            catch (Exception exception)
            {
                _logger.Warn($"Could not close gamepad {id}: {exception.Message}");
            }
        }

        foreach (var id in _failures.Keys.Where(id => !_detectedIds.Contains(id)).ToList())
        {
            _failures.Remove(id);
        }
    }

    private void TryOpenPad(uint id)
    {
        var handle = Sdl3.SDL_OpenGamepad(id);
        if (handle == IntPtr.Zero)
        {
            var error = Sdl3.GetErrorString();
            var firstFailure = !_failures.ContainsKey(id);
            _failures[id] = new FailedOpen(Environment.TickCount64 + 5000, error);
            if (firstFailure)
            {
                _logger.Warn($"Could not open controller id {id}: {error}. Continuing with other controllers.");
            }

            return;
        }

        _failures.Remove(id);
        var name = Sdl3.GetName(handle);
        var type = Sdl3.SDL_GetGamepadType(handle);
        var pad = new OpenPad(id, handle, name, type, PadFamilyMapper.FromSdlType(type));
        _pads[id] = pad;
        ReadBattery(pad, force: true);
        _logger.Info($"Controller connected: {name} ({PadFamilyMapper.TypeName(type)}), id {id}.");
    }

    private ControllerSnapshot BuildSnapshot()
    {
        var inputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (button, inputId) in ButtonMap)
        {
            foreach (var pad in _pads.Values)
            {
                if (Sdl3.SDL_GetGamepadButton(pad.Handle, button) != 0)
                {
                    inputs.Add(inputId);
                    break;
                }
            }
        }

        var maxLeftX = 0.0;
        var minLeftX = 0.0;
        var maxLeftY = 0.0;
        var minLeftY = 0.0;
        var maxRightX = 0.0;
        var minRightX = 0.0;
        var maxRightY = 0.0;
        var minRightY = 0.0;
        var maxLeftTrigger = 0.0;
        var maxRightTrigger = 0.0;

        foreach (var pad in _pads.Values)
        {
            var leftX = NormalizeAxis(Sdl3.SDL_GetGamepadAxis(pad.Handle, Sdl3.GamepadAxisLeftX));
            var leftY = NormalizeAxis(Sdl3.SDL_GetGamepadAxis(pad.Handle, Sdl3.GamepadAxisLeftY));
            var rightX = NormalizeAxis(Sdl3.SDL_GetGamepadAxis(pad.Handle, Sdl3.GamepadAxisRightX));
            var rightY = NormalizeAxis(Sdl3.SDL_GetGamepadAxis(pad.Handle, Sdl3.GamepadAxisRightY));
            var leftTrigger = NormalizeTrigger(Sdl3.SDL_GetGamepadAxis(pad.Handle, Sdl3.GamepadAxisLeftTrigger));
            var rightTrigger = NormalizeTrigger(Sdl3.SDL_GetGamepadAxis(pad.Handle, Sdl3.GamepadAxisRightTrigger));

            maxLeftX = Math.Max(maxLeftX, leftX);
            minLeftX = Math.Min(minLeftX, leftX);
            maxLeftY = Math.Max(maxLeftY, leftY);
            minLeftY = Math.Min(minLeftY, leftY);
            maxRightX = Math.Max(maxRightX, rightX);
            minRightX = Math.Min(minRightX, rightX);
            maxRightY = Math.Max(maxRightY, rightY);
            minRightY = Math.Min(minRightY, rightY);
            maxLeftTrigger = Math.Max(maxLeftTrigger, leftTrigger);
            maxRightTrigger = Math.Max(maxRightTrigger, rightTrigger);

            ReadBattery(pad, force: false);
        }

        if (_leftStickRight.Update(maxLeftX)) inputs.Add("left_stick_right");
        if (_leftStickLeft.Update(-minLeftX)) inputs.Add("left_stick_left");
        if (_leftStickDown.Update(maxLeftY)) inputs.Add("left_stick_down");
        if (_leftStickUp.Update(-minLeftY)) inputs.Add("left_stick_up");
        if (_rightStickRight.Update(maxRightX)) inputs.Add("right_stick_right");
        if (_rightStickLeft.Update(-minRightX)) inputs.Add("right_stick_left");
        if (_rightStickDown.Update(maxRightY)) inputs.Add("right_stick_down");
        if (_rightStickUp.Update(-minRightY)) inputs.Add("right_stick_up");

        _l2Active = UpdateTrigger(maxLeftTrigger, _l2Active, _l2Threshold);
        _r2Active = UpdateTrigger(maxRightTrigger, _r2Active, _r2Threshold);
        if (_l2Active) inputs.Add("l2");
        if (_r2Active) inputs.Add("r2");

        var orderedInputs = InputCatalog.All
            .Where(definition => inputs.Contains(definition.Id))
            .Select(definition => definition.Id)
            .ToList();

        var pads = _pads.Values
            .OrderBy(pad => pad.InstanceId)
            .Select(pad => new PadInfo(
                pad.InstanceId,
                pad.Name,
                pad.Family,
                PadFamilyMapper.TypeName(pad.SdlType),
                pad.BatteryPercent,
                PadFamilyMapper.PowerStateName(pad.PowerState)))
            .ToList();

        return new ControllerSnapshot(
            Environment.TickCount64,
            orderedInputs,
            pads,
            _detectedIds.Count,
            _sdlVersion,
            null);
    }

    private void ReadBattery(OpenPad pad, bool force)
    {
        var now = Environment.TickCount64;
        if (!force && now - pad.LastBatteryReadMs < BatteryIntervalMs)
        {
            return;
        }

        pad.LastBatteryReadMs = now;
        try
        {
            var state = Sdl3.SDL_GetGamepadPowerInfo(pad.Handle, out var percent);
            pad.PowerState = state;
            pad.BatteryPercent = percent is >= 0 and <= 100 ? percent : null;
        }
        catch
        {
            pad.BatteryPercent = null;
            pad.PowerState = Sdl3.PowerStateUnknown;
        }
    }

    private static double NormalizeAxis(short value)
    {
        return Math.Clamp(value / 32767.0, -1.0, 1.0);
    }

    private static double NormalizeTrigger(short value)
    {
        return Math.Clamp(value / 32767.0, 0.0, 1.0);
    }

    private static bool UpdateTrigger(double value, bool current, double threshold)
    {
        var activate = Math.Clamp(threshold, 0.05, 0.99);
        var release = Math.Max(0.01, activate - 0.05);
        return current ? value >= release : value >= activate;
    }

    private void CloseAllPads()
    {
        foreach (var pad in _pads.Values)
        {
            try
            {
                Sdl3.SDL_CloseGamepad(pad.Handle);
            }
            catch
            {
                // Shutting down; nothing useful to do.
            }
        }

        _pads.Clear();
        _failures.Clear();
        _detectedIds.Clear();
    }

    public void Dispose()
    {
        _stop = true;
        Thread? thread;
        lock (_gate)
        {
            thread = _thread;
        }

        if (thread is not null && thread.IsAlive)
        {
            try
            {
                thread.Join(2000);
            }
            catch (Exception exception)
            {
                _logger.Warn("Could not join the SDL thread: " + exception.Message);
            }
        }
    }

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint milliseconds);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint milliseconds);

    private sealed class OpenPad
    {
        public OpenPad(uint instanceId, IntPtr handle, string name, int sdlType, PadFamily family)
        {
            InstanceId = instanceId;
            Handle = handle;
            Name = name;
            SdlType = sdlType;
            Family = family;
        }

        public uint InstanceId { get; }
        public IntPtr Handle { get; }
        public string Name { get; }
        public int SdlType { get; }
        public PadFamily Family { get; }
        public long LastBatteryReadMs { get; set; }
        public int? BatteryPercent { get; set; }
        public int PowerState { get; set; } = Sdl3.PowerStateUnknown;
    }

    private sealed record FailedOpen(long NextRetryMs, string Error);

    private sealed class SdlFailureException : Exception
    {
        public SdlFailureException(string message) : base(message)
        {
        }
    }
}
