namespace NashmiKeys.Core.Engine;

public enum EngineEventKind
{
    Activated,
    Deactivated,
    Paused,
    Resumed
}

public sealed record EngineEvent(
    EngineEventKind Kind,
    string? HotkeyId,
    string? HotkeyName,
    string? Reason);
