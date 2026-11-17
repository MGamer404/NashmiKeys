namespace NashmiKeys.Core.Input;

public sealed record KeyDefinition(
    string Name,
    ushort VirtualKey,
    ushort ScanCode,
    bool Extended,
    int ModifierSlot)
{
    public bool IsModifier => ModifierSlot >= 0;
}
