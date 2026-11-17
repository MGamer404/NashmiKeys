using NashmiKeys.Core.Configuration;
using NashmiKeys.Core.Engine;
using NashmiKeys.Core.Logging;
using NashmiKeys.Core.Sdl;

namespace NashmiKeys.App.Services;

/// <summary>
/// Wires the SDL polling thread to the engine. All engine calls happen on the
/// polling thread; UI listeners marshal their own events.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private readonly HotkeyEngine _engine;
    private readonly GamepadPoller _poller;
    private readonly ILogger _logger;
    private readonly object _stateGate = new();
    private bool _paused;
    private bool _suppressed;
    private bool _started;
    private volatile ControllerSnapshot? _latest;
    private string? _lastSdlError;

    public HotkeyService(
        HotkeyEngine engine,
        GamepadPoller poller,
        ILogger logger,
        bool initiallyPaused)
    {
        _engine = engine;
        _poller = poller;
        _logger = logger;
        _paused = initiallyPaused;
        _poller.SnapshotProduced += OnSnapshot;
        _engine.SetPaused(initiallyPaused);
    }

    public event Action<EngineEvent>? HotkeyEvent;

    /// <summary>Raised on the polling thread when one or more inputs go down.</summary>
    public event Action<IReadOnlyList<string>>? InputsPressed;

    public ControllerSnapshot? LatestSnapshot => _latest;

    public string? LastSdlError => _lastSdlError ?? _poller.LastError;

    public string SdlVersion => _poller.SdlVersion;

    public bool IsPaused
    {
        get
        {
            lock (_stateGate)
            {
                return _paused;
            }
        }
    }

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _poller.Start();
    }

    public void SetPaused(bool paused)
    {
        List<EngineEvent> events;
        lock (_stateGate)
        {
            if (_paused == paused)
            {
                return;
            }

            _paused = paused;
            events = _engine.SetPaused(_paused || _suppressed).ToList();
        }

        Raise(events);
    }

    public void SetSuppressed(bool suppressed)
    {
        List<EngineEvent> events;
        lock (_stateGate)
        {
            if (_suppressed == suppressed)
            {
                return;
            }

            _suppressed = suppressed;
            events = _engine.SetPaused(_paused || _suppressed).ToList();
        }

        Raise(events);
    }

    public void UpdateHotkeys(IEnumerable<Hotkey> hotkeys)
    {
        var events = _engine.ReplaceHotkeys(hotkeys);
        Raise(events);
    }

    public void UpdateSettings(AppSettings settings)
    {
        _engine.SetComboHoldMs(settings.ComboHoldMs);
        _poller.UpdateThresholds(settings.L2Threshold, settings.R2Threshold);
    }

    public void ReleaseAll(string reason)
    {
        Raise(_engine.ReleaseAll(reason));
    }

    private void OnSnapshot(ControllerSnapshot snapshot)
    {
        try
        {
            var previous = _latest;
            _latest = snapshot;
            _lastSdlError = _poller.LastError ?? snapshot.Error;

            if (previous is not null && snapshot.ConnectedCount < previous.ConnectedCount)
            {
                _logger.Warn("A controller disconnected; releasing all injected keys.");
                Raise(_engine.ReleaseAll("controller disconnected"));
            }

            if (previous is not null)
            {
                var newlyPressed = snapshot.Inputs
                    .Where(input => !previous.Inputs.Contains(input, StringComparer.OrdinalIgnoreCase))
                    .ToList();
                if (newlyPressed.Count > 0)
                {
                    try
                    {
                        InputsPressed?.Invoke(newlyPressed);
                    }
                    catch (Exception exception)
                    {
                        _logger.Error("An input-pressed subscriber failed.", exception);
                    }
                }
            }

            Raise(_engine.Tick(snapshot.Inputs, snapshot.TimestampMs));
        }
        catch (Exception exception)
        {
            _logger.Error("Hotkey processing failed; releasing all keys.", exception);
            try
            {
                Raise(_engine.ReleaseAll("unhandled error"));
            }
            catch (Exception releaseException)
            {
                _logger.Error("Could not release keys after a processing error.", releaseException);
            }
        }
    }

    private void Raise(IReadOnlyList<EngineEvent> events)
    {
        if (events.Count == 0)
        {
            return;
        }

        var handler = HotkeyEvent;
        if (handler is null)
        {
            return;
        }

        foreach (var engineEvent in events)
        {
            try
            {
                handler(engineEvent);
            }
            catch (Exception exception)
            {
                _logger.Error("Hotkey event subscriber failed.", exception);
            }
        }
    }

    public void Dispose()
    {
        try
        {
            _poller.Dispose();
        }
        catch (Exception exception)
        {
            _logger.Warn("Stopping the SDL poller failed: " + exception.Message);
        }

        try
        {
            _engine.ReleaseAll("exiting");
            _engine.Dispose();
        }
        catch (Exception exception)
        {
            _logger.Error("Releasing keys while exiting failed.", exception);
        }
    }
}
