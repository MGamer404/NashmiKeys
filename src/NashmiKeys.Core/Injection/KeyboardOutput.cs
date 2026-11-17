using NashmiKeys.Core.Input;

namespace NashmiKeys.Core.Injection;

/// <summary>
/// Tracks every key this app has injected. Never presses a key twice and
/// always releases in exact reverse press order.
/// </summary>
public sealed class KeyboardOutput
{
    private readonly IKeyInjector _injector;
    private readonly List<KeyDefinition> _held = new();
    private readonly HashSet<string> _heldNames = new(StringComparer.OrdinalIgnoreCase);

    public KeyboardOutput(IKeyInjector injector)
    {
        _injector = injector;
    }

    public IReadOnlyList<KeyDefinition> Held => _held;

    public bool IsHeld(string keyName)
    {
        return _heldNames.Contains(keyName);
    }

    public void Press(KeyDefinition key)
    {
        if (!_heldNames.Add(key.Name))
        {
            return;
        }

        try
        {
            _injector.KeyDown(key);
            _held.Add(key);
        }
        catch
        {
            _heldNames.Remove(key.Name);
            throw;
        }
    }

    public void Release(string keyName)
    {
        var index = FindLastIndex(keyName);
        if (index < 0)
        {
            return;
        }

        var key = _held[index];
        _injector.KeyUp(key);
        _held.RemoveAt(index);
        _heldNames.Remove(key.Name);
    }

    public void ReleaseAll()
    {
        for (var index = _held.Count - 1; index >= 0; index--)
        {
            var key = _held[index];
            try
            {
                _injector.KeyUp(key);
            }
            catch
            {
                // Keep releasing the remaining keys; the injector logs the failure.
            }
            finally
            {
                _held.RemoveAt(index);
                _heldNames.Remove(key.Name);
            }
        }
    }

    private int FindLastIndex(string keyName)
    {
        for (var index = _held.Count - 1; index >= 0; index--)
        {
            if (string.Equals(_held[index].Name, keyName, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }
}
