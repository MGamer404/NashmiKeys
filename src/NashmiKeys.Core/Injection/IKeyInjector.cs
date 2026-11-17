using NashmiKeys.Core.Input;

namespace NashmiKeys.Core.Injection;

/// <summary>
/// Abstraction over real keyboard injection so tests can record exact order.
/// </summary>
public interface IKeyInjector
{
    void KeyDown(KeyDefinition key);
    void KeyUp(KeyDefinition key);
}
