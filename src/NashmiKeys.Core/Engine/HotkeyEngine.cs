using NashmiKeys.Core.Configuration;
using NashmiKeys.Core.Injection;
using NashmiKeys.Core.Input;
using NashmiKeys.Core.Logging;

namespace NashmiKeys.Core.Engine;

/// <summary>
/// UI-free chord matcher and injection state machine. One hotkey can be active
/// at a time. Tick() is driven by the controller polling thread.
/// </summary>
public sealed class HotkeyEngine : IDisposable
{
    public const int TapDwellMs = 40;

    private readonly object _gate = new();
    private readonly KeyboardOutput _output;
    private readonly ILogger _logger;
    private List<CompiledHotkey> _hotkeys;
    private int _comboHoldMs;
    private bool _paused;
    private bool _disposed;

    private CompiledHotkey? _active;
    private long _tapReleaseAtMs;
    private bool _toggleAwaitingRelease;

    private HashSet<string> _tapLock = new(StringComparer.OrdinalIgnoreCase);
    private string? _toggleReleaseLockId;
    private HashSet<string> _toggleReleaseLockInputs = new(StringComparer.OrdinalIgnoreCase);

    private string? _pendingActivationId;
    private long _pendingActivationSinceMs;
    private string? _pendingToggleOffId;
    private long _pendingToggleOffSinceMs;

    public HotkeyEngine(
        IEnumerable<Hotkey> hotkeys,
        IKeyInjector injector,
        int comboHoldMs = 0,
        ILogger? logger = null)
    {
        _output = new KeyboardOutput(injector);
        _logger = logger ?? NullLogger.Instance;
        _comboHoldMs = Math.Clamp(comboHoldMs, 0, 500);
        _hotkeys = Compile(hotkeys);
    }

    public bool IsPaused
    {
        get
        {
            lock (_gate)
            {
                return _paused;
            }
        }
    }

    public bool IsActive
    {
        get
        {
            lock (_gate)
            {
                return _active is not null;
            }
        }
    }

    public string? ActiveHotkeyId
    {
        get
        {
            lock (_gate)
            {
                return _active?.Model.Id;
            }
        }
    }

    public IReadOnlyList<string> HeldOutputKeys
    {
        get
        {
            lock (_gate)
            {
                return _output.Held.Select(key => key.Name).ToList();
            }
        }
    }

    public IReadOnlyList<EngineEvent> Tick(IReadOnlyCollection<string> pressedInputs, long timestampMs)
    {
        lock (_gate)
        {
            var events = new List<EngineEvent>();
            if (_paused || _disposed)
            {
                return events;
            }

            var pressed = new HashSet<string>(pressedInputs, StringComparer.OrdinalIgnoreCase);
            var deactivatedThisTick = false;

            if (_active is not null)
            {
                deactivatedThisTick = ProcessActive(pressed, timestampMs, events);
            }

            if (_tapLock.Count > 0 && !_tapLock.Overlaps(pressed))
            {
                _tapLock.Clear();
            }

            if (_toggleReleaseLockId is not null && !_toggleReleaseLockInputs.Overlaps(pressed))
            {
                _toggleReleaseLockId = null;
                _toggleReleaseLockInputs.Clear();
            }

            if (_active is null && !deactivatedThisTick && _tapLock.Count == 0)
            {
                ProcessIdle(pressed, timestampMs, events);
            }

            return events;
        }
    }

    /// <summary>
    /// Returns true when the previously active hotkey was deactivated in this
    /// tick (which stops another hotkey activating until the next tick).
    /// </summary>
    private bool ProcessActive(HashSet<string> pressed, long nowMs, List<EngineEvent> events)
    {
        var active = _active!;

        if (active.Model.Mode == HotkeyMode.Tap)
        {
            var chordBroken = !active.InputSet.IsSubsetOf(pressed);
            if (chordBroken || nowMs >= _tapReleaseAtMs)
            {
                Deactivate("tap", events);
                _tapLock = new HashSet<string>(active.InputSet, StringComparer.OrdinalIgnoreCase);
                return true;
            }

            return false;
        }

        if (active.Model.Mode == HotkeyMode.Hold)
        {
            if (!active.InputSet.IsSubsetOf(pressed))
            {
                Deactivate("released", events);
                return true;
            }

            return false;
        }

        // Toggle: outputs stay held until the chord is pressed a second time.
        if (_toggleAwaitingRelease)
        {
            if (!active.InputSet.Overlaps(pressed))
            {
                _toggleAwaitingRelease = false;
            }
            else
            {
                _pendingToggleOffId = null;
                return false;
            }
        }

        if (active.InputSet.IsSubsetOf(pressed))
        {
            if (ReadyAfterHold(
                    active.Model.Id,
                    ref _pendingToggleOffId,
                    ref _pendingToggleOffSinceMs,
                    nowMs))
            {
                var id = active.Model.Id;
                var inputs = new HashSet<string>(active.InputSet, StringComparer.OrdinalIgnoreCase);
                Deactivate("toggle", events);
                _toggleReleaseLockId = id;
                _toggleReleaseLockInputs = inputs;
                _pendingToggleOffId = null;
                return true;
            }
        }
        else
        {
            _pendingToggleOffId = null;
        }

        return false;
    }

    private void ProcessIdle(HashSet<string> pressed, long nowMs, List<EngineEvent> events)
    {
        var candidate = SelectCandidate(pressed);
        if (candidate is null)
        {
            _pendingActivationId = null;
            return;
        }

        if (!ReadyAfterHold(
                candidate.Model.Id,
                ref _pendingActivationId,
                ref _pendingActivationSinceMs,
                nowMs))
        {
            return;
        }

        _pendingActivationId = null;

        if (!TryPressOutputs(candidate.Model, events))
        {
            return;
        }

        _active = candidate;
        _toggleAwaitingRelease = candidate.Model.Mode == HotkeyMode.Toggle;
        _tapReleaseAtMs = candidate.Model.Mode == HotkeyMode.Tap ? nowMs + TapDwellMs : 0;
        _pendingToggleOffId = null;

        _logger.Info($"Hotkey activated: {DisplayName(candidate.Model)} ({candidate.Model.Mode}).");
        events.Add(new EngineEvent(
            EngineEventKind.Activated,
            candidate.Model.Id,
            DisplayName(candidate.Model),
            candidate.Model.Mode.ToString()));
    }

    private bool TryPressOutputs(Hotkey hotkey, List<EngineEvent> events)
    {
        try
        {
            foreach (var key in KeyCatalog.OrderForPress(hotkey.Outputs))
            {
                _output.Press(key);
            }

            return true;
        }
        catch (Exception exception)
        {
            _logger.Error($"Could not inject outputs for '{DisplayName(hotkey)}'.", exception);
            try
            {
                _output.ReleaseAll();
            }
            catch (Exception releaseException)
            {
                _logger.Error("Could not release partially injected keys.", releaseException);
            }

            return false;
        }
    }

    private void Deactivate(string reason, List<EngineEvent> events)
    {
        var active = _active;
        try
        {
            _output.ReleaseAll();
        }
        catch (Exception exception)
        {
            _logger.Error("Could not release injected keys during deactivation.", exception);
        }

        _active = null;
        _toggleAwaitingRelease = false;
        _tapReleaseAtMs = 0;
        _pendingToggleOffId = null;

        if (active is not null)
        {
            _logger.Info($"Hotkey deactivated: {DisplayName(active.Model)} ({reason}).");
            events.Add(new EngineEvent(
                EngineEventKind.Deactivated,
                active.Model.Id,
                DisplayName(active.Model),
                reason));
        }
    }

    private CompiledHotkey? SelectCandidate(HashSet<string> pressed)
    {
        CompiledHotkey? best = null;
        var bestCount = -1;

        foreach (var hotkey in _hotkeys)
        {
            if (!hotkey.Model.Enabled)
            {
                continue;
            }

            if (hotkey.InputSet.Count == 0 || hotkey.Model.Outputs.Count == 0)
            {
                continue;
            }

            if (_toggleReleaseLockId is not null &&
                string.Equals(hotkey.Model.Id, _toggleReleaseLockId, StringComparison.Ordinal))
            {
                continue;
            }

            if (!hotkey.InputSet.IsSubsetOf(pressed))
            {
                continue;
            }

            if (hotkey.InputSet.Count > bestCount)
            {
                best = hotkey;
                bestCount = hotkey.InputSet.Count;
            }
        }

        return best;
    }

    private bool ReadyAfterHold(string id, ref string? pendingId, ref long pendingSinceMs, long nowMs)
    {
        if (_comboHoldMs <= 0)
        {
            return true;
        }

        if (!string.Equals(pendingId, id, StringComparison.Ordinal))
        {
            pendingId = id;
            pendingSinceMs = nowMs;
            return false;
        }

        return nowMs - pendingSinceMs >= _comboHoldMs;
    }

    private static string DisplayName(Hotkey hotkey)
    {
        return string.IsNullOrWhiteSpace(hotkey.Name) ? "Untitled hotkey" : hotkey.Name.Trim();
    }

    public IReadOnlyList<EngineEvent> SetPaused(bool paused, long timestampMs = 0)
    {
        lock (_gate)
        {
            var events = new List<EngineEvent>();
            if (_paused == paused)
            {
                return events;
            }

            _paused = paused;
            if (paused)
            {
                Deactivate("paused", events);
                _output.ReleaseAll();
                ClearRuntimeState();
                _logger.Info("Hotkeys paused.");
                events.Add(new EngineEvent(EngineEventKind.Paused, null, null, null));
            }
            else
            {
                ClearRuntimeState();
                _logger.Info("Hotkeys resumed.");
                events.Add(new EngineEvent(EngineEventKind.Resumed, null, null, null));
            }

            return events;
        }
    }

    public IReadOnlyList<EngineEvent> ReplaceHotkeys(IEnumerable<Hotkey> hotkeys, long timestampMs = 0)
    {
        lock (_gate)
        {
            var events = new List<EngineEvent>();
            Deactivate("hotkeys changed", events);
            _output.ReleaseAll();
            ClearRuntimeState();
            _hotkeys = Compile(hotkeys);
            _logger.Info($"Hotkey list reloaded: {_hotkeys.Count} entries.");
            return events;
        }
    }

    public IReadOnlyList<EngineEvent> SetComboHoldMs(int milliseconds)
    {
        lock (_gate)
        {
            _comboHoldMs = Math.Clamp(milliseconds, 0, 500);
            _pendingActivationId = null;
            _pendingToggleOffId = null;
            return Array.Empty<EngineEvent>();
        }
    }

    public IReadOnlyList<EngineEvent> ReleaseAll(string reason)
    {
        lock (_gate)
        {
            var events = new List<EngineEvent>();
            Deactivate(reason, events);
            _output.ReleaseAll();
            ClearRuntimeState();
            return events;
        }
    }

    private void ClearRuntimeState()
    {
        _active = null;
        _toggleAwaitingRelease = false;
        _tapReleaseAtMs = 0;
        _tapLock.Clear();
        _toggleReleaseLockId = null;
        _toggleReleaseLockInputs.Clear();
        _pendingActivationId = null;
        _pendingActivationSinceMs = 0;
        _pendingToggleOffId = null;
        _pendingToggleOffSinceMs = 0;
    }

    private static List<CompiledHotkey> Compile(IEnumerable<Hotkey> hotkeys)
    {
        var result = new List<CompiledHotkey>();
        foreach (var hotkey in hotkeys)
        {
            var clone = hotkey.Clone();
            result.Add(new CompiledHotkey(clone, new HashSet<string>(
                InputCatalog.NormalizeSet(clone.Inputs),
                StringComparer.OrdinalIgnoreCase)));
        }

        return result;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                _output.ReleaseAll();
            }
            catch (Exception exception)
            {
                _logger.Error("Could not release keys while disposing the engine.", exception);
            }

            _disposed = true;
            ClearRuntimeState();
        }
    }

    private sealed class CompiledHotkey
    {
        public CompiledHotkey(Hotkey model, HashSet<string> inputSet)
        {
            Model = model;
            InputSet = inputSet;
        }

        public Hotkey Model { get; }
        public HashSet<string> InputSet { get; }
    }
}
