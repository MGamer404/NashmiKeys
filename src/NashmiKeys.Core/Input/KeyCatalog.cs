namespace NashmiKeys.Core.Input;

/// <summary>
/// Curated keyboard output catalog. Names are the stable values written to
/// config.json; scan codes are Set 1 codes without the 0xE0 prefix. Extended
/// keys are flagged so SendInput can add KEYEVENTF_EXTENDEDKEY.
/// </summary>
public static class KeyCatalog
{
    public const int WinModifierSlot = 0;
    public const int CtrlModifierSlot = 1;
    public const int AltModifierSlot = 2;
    public const int ShiftModifierSlot = 3;

    public static readonly IReadOnlyList<KeyDefinition> All = Build();

    private static readonly Dictionary<string, KeyDefinition> ByName =
        All.ToDictionary(item => item.Name, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<ushort, List<KeyDefinition>> ByVirtualKey = BuildVirtualKeyMap();

    private static List<KeyDefinition> Build()
    {
        var keys = new List<KeyDefinition>
        {
            Key("Win", 0x5B, 0x5B, extended: true, modifierSlot: WinModifierSlot),
            Key("Ctrl", 0x11, 0x1D, modifierSlot: CtrlModifierSlot),
            Key("Alt", 0x12, 0x38, modifierSlot: AltModifierSlot),
            Key("Shift", 0x10, 0x2A, modifierSlot: ShiftModifierSlot),

            Key("Tab", 0x09, 0x0F),
            Key("Esc", 0x1B, 0x01),
            Key("Enter", 0x0D, 0x1C),
            Key("Space", 0x20, 0x39),
            Key("Backspace", 0x08, 0x0E),
            Key("Delete", 0x2E, 0x53, extended: true),
            Key("Insert", 0x2D, 0x52, extended: true),
            Key("Home", 0x24, 0x47, extended: true),
            Key("End", 0x23, 0x4F, extended: true),
            Key("PageUp", 0x21, 0x49, extended: true),
            Key("PageDown", 0x22, 0x51, extended: true),
            Key("Up", 0x26, 0x48, extended: true),
            Key("Down", 0x28, 0x50, extended: true),
            Key("Left", 0x25, 0x4B, extended: true),
            Key("Right", 0x27, 0x4D, extended: true),
            Key("CapsLock", 0x14, 0x3A),
            Key("PrintScreen", 0x2C, 0x37, extended: true),
            Key("ScrollLock", 0x91, 0x46)
        };

        var functionScanCodes = new ushort[]
        {
            0x3B, 0x3C, 0x3D, 0x3E, 0x3F, 0x40, 0x41, 0x42, 0x43, 0x44,
            0x57, 0x58, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6A, 0x6B,
            0x6C, 0x6D, 0x6E, 0x6F
        };

        for (var index = 0; index < 24; index++)
        {
            keys.Add(Key($"F{index + 1}", (ushort)(0x70 + index), functionScanCodes[index]));
        }

        var letterScanCodes = new Dictionary<char, ushort>
        {
            ['A'] = 0x1E, ['B'] = 0x30, ['C'] = 0x2E, ['D'] = 0x20, ['E'] = 0x12,
            ['F'] = 0x21, ['G'] = 0x22, ['H'] = 0x23, ['I'] = 0x17, ['J'] = 0x24,
            ['K'] = 0x25, ['L'] = 0x26, ['M'] = 0x32, ['N'] = 0x31, ['O'] = 0x18,
            ['P'] = 0x19, ['Q'] = 0x10, ['R'] = 0x13, ['S'] = 0x1F, ['T'] = 0x14,
            ['U'] = 0x16, ['V'] = 0x2F, ['W'] = 0x11, ['X'] = 0x2D, ['Y'] = 0x15,
            ['Z'] = 0x2C
        };

        foreach (var letter in letterScanCodes.Keys.OrderBy(character => character))
        {
            keys.Add(Key(letter.ToString(), (ushort)letter, letterScanCodes[letter]));
        }

        var digitScanCodes = new ushort[] { 0x0B, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A };
        for (var digit = 0; digit <= 9; digit++)
        {
            keys.Add(Key(digit.ToString(), (ushort)(0x30 + digit), digitScanCodes[digit]));
        }

        var numpadScanCodes = new ushort[] { 0x52, 0x4F, 0x50, 0x51, 0x4B, 0x4C, 0x4D, 0x47, 0x48, 0x49 };
        for (var digit = 0; digit <= 9; digit++)
        {
            keys.Add(Key($"Num{digit}", (ushort)(0x60 + digit), numpadScanCodes[digit], extended: true));
        }

        keys.Add(Key("NumMultiply", 0x6A, 0x37, extended: true));
        keys.Add(Key("NumAdd", 0x6B, 0x4E, extended: true));
        keys.Add(Key("NumSubtract", 0x6D, 0x4A, extended: true));
        keys.Add(Key("NumDecimal", 0x6E, 0x53, extended: true));
        keys.Add(Key("NumDivide", 0x6F, 0x35, extended: true));
        keys.Add(Key("NumEnter", 0x0D, 0x1C, extended: true));
        keys.Add(Key("NumLock", 0x90, 0x45, extended: true));

        keys.Add(Key("Semicolon", 0xBA, 0x27));
        keys.Add(Key("Equal", 0xBB, 0x0D));
        keys.Add(Key("Comma", 0xBC, 0x33));
        keys.Add(Key("Minus", 0xBD, 0x0C));
        keys.Add(Key("Period", 0xBE, 0x34));
        keys.Add(Key("Slash", 0xBF, 0x35));
        keys.Add(Key("Backtick", 0xC0, 0x29));
        keys.Add(Key("LeftBracket", 0xDB, 0x1A));
        keys.Add(Key("Backslash", 0xDC, 0x2B));
        keys.Add(Key("RightBracket", 0xDD, 0x1B));
        keys.Add(Key("Quote", 0xDE, 0x28));

        return keys;
    }

    private static KeyDefinition Key(
        string name,
        ushort virtualKey,
        ushort scanCode,
        bool extended = false,
        int modifierSlot = -1)
    {
        return new KeyDefinition(name, virtualKey, scanCode, extended, modifierSlot);
    }

    private static Dictionary<ushort, List<KeyDefinition>> BuildVirtualKeyMap()
    {
        var result = new Dictionary<ushort, List<KeyDefinition>>();
        foreach (var key in All)
        {
            var normalizedVk = NormalizeVirtualKey(key.VirtualKey);
            if (!result.TryGetValue(normalizedVk, out var list))
            {
                list = new List<KeyDefinition>();
                result[normalizedVk] = list;
            }

            list.Add(key);
        }

        return result;
    }

    private static ushort NormalizeVirtualKey(ushort virtualKey)
    {
        return virtualKey switch
        {
            0xA0 or 0xA1 => 0x10, // L/R shift -> Shift
            0xA2 or 0xA3 => 0x11, // L/R control -> Ctrl
            0xA4 or 0xA5 => 0x12, // L/R alt -> Alt
            0x5C => 0x5B,         // right win -> Win
            _ => virtualKey
        };
    }

    public static bool TryGet(string? name, out KeyDefinition definition)
    {
        definition = null!;
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        return ByName.TryGetValue(name.Trim(), out definition!);
    }

    public static KeyDefinition? Get(string name)
    {
        return TryGet(name, out var definition) ? definition : null;
    }

    public static string CanonicalName(string name)
    {
        return TryGet(name, out var definition) ? definition.Name : name.Trim();
    }

    public static bool TryFromKeyboard(uint virtualKey, uint scanCode, bool isExtended, out KeyDefinition definition)
    {
        definition = null!;
        var normalized = NormalizeVirtualKey((ushort)(virtualKey & 0xFFFF));

        if (normalized == 0x0D)
        {
            var name = isExtended ? "NumEnter" : "Enter";
            return TryGet(name, out definition);
        }

        if (!ByVirtualKey.TryGetValue(normalized, out var candidates) || candidates.Count == 0)
        {
            return false;
        }

        if (candidates.Count == 1)
        {
            definition = candidates[0];
            return true;
        }

        definition = candidates.FirstOrDefault(candidate => candidate.Extended == isExtended) ?? candidates[0];
        return true;
    }

    /// <summary>Outputs normalized to canonical catalog names, duplicates removed, order preserved.</summary>
    public static IReadOnlyList<string> NormalizeOutputs(IEnumerable<string> outputs)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var output in outputs)
        {
            if (!TryGet(output, out var key))
            {
                continue;
            }

            if (seen.Add(key.Name))
            {
                result.Add(key.Name);
            }
        }

        return result;
    }

    /// <summary>
    /// Orders keys for injection: Win, Ctrl, Alt, Shift first (in that order),
    /// then every other key in the order the user listed it.
    /// </summary>
    public static IReadOnlyList<KeyDefinition> OrderForPress(IEnumerable<string> outputs)
    {
        var keys = NormalizeOutputs(outputs)
            .Select(name => Get(name)!)
            .ToList();

        return keys.Where(key => key.IsModifier)
            .OrderBy(key => key.ModifierSlot)
            .Concat(keys.Where(key => !key.IsModifier))
            .ToList();
    }
}
