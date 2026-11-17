namespace NashmiKeys.Core.Input;

public sealed record InputDefinition(
    string Id,
    string XboxLabel,
    string PlayStationLabel,
    string NintendoLabel)
{
    public string Label(PadFamily family)
    {
        return family switch
        {
            PadFamily.PlayStation => PlayStationLabel,
            PadFamily.Nintendo => NintendoLabel,
            _ => XboxLabel
        };
    }
}

public static class InputCatalog
{
    // Order is also the display order used everywhere in the UI.
    public static readonly IReadOnlyList<InputDefinition> All = new List<InputDefinition>
    {
        new("south", "A", "Cross", "B"),
        new("east", "B", "Circle", "A"),
        new("west", "X", "Square", "Y"),
        new("north", "Y", "Triangle", "X"),
        new("l1", "LB", "L1", "L"),
        new("r1", "RB", "R1", "R"),
        new("l2", "LT", "L2", "ZL"),
        new("r2", "RT", "R2", "ZR"),
        new("l3", "LS", "L3", "L3"),
        new("r3", "RS", "R3", "R3"),
        new("start", "Menu", "Options", "Plus"),
        new("back", "View", "Create", "Minus"),
        new("guide", "Guide", "PS", "Home"),
        new("touchpad", "Touchpad", "Touchpad", "Touchpad"),
        new("dpad_up", "D-pad Up", "D-pad Up", "D-pad Up"),
        new("dpad_down", "D-pad Down", "D-pad Down", "D-pad Down"),
        new("dpad_left", "D-pad Left", "D-pad Left", "D-pad Left"),
        new("dpad_right", "D-pad Right", "D-pad Right", "D-pad Right"),
        new("left_stick_up", "Left Stick Up", "Left Stick Up", "Left Stick Up"),
        new("left_stick_down", "Left Stick Down", "Left Stick Down", "Left Stick Down"),
        new("left_stick_left", "Left Stick Left", "Left Stick Left", "Left Stick Left"),
        new("left_stick_right", "Left Stick Right", "Left Stick Right", "Left Stick Right"),
        new("right_stick_up", "Right Stick Up", "Right Stick Up", "Right Stick Up"),
        new("right_stick_down", "Right Stick Down", "Right Stick Down", "Right Stick Down"),
        new("right_stick_left", "Right Stick Left", "Right Stick Left", "Right Stick Left"),
        new("right_stick_right", "Right Stick Right", "Right Stick Right", "Right Stick Right"),
        new("misc1", "Share", "Share", "Capture"),
        new("paddle1", "P1", "P1", "P1"),
        new("paddle2", "P2", "P2", "P2"),
        new("paddle3", "P3", "P3", "P3"),
        new("paddle4", "P4", "P4", "P4")
    };

    private static readonly Dictionary<string, InputDefinition> ById =
        All.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);

    public static bool IsKnown(string? id)
    {
        return !string.IsNullOrWhiteSpace(id) && ById.ContainsKey(id.Trim());
    }

    public static string NormalizeId(string id)
    {
        return id.Trim().ToLowerInvariant();
    }

    public static InputDefinition? Get(string id)
    {
        return ById.TryGetValue(id.Trim(), out var definition) ? definition : null;
    }

    public static string Label(string id, PadFamily family)
    {
        var definition = Get(id);
        return definition?.Label(family) ?? id;
    }

    public static int SortOrder(string id)
    {
        for (var index = 0; index < All.Count; index++)
        {
            if (string.Equals(All[index].Id, id, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return int.MaxValue;
    }

    public static PadFamily DefaultFamily => PadFamily.Xbox;

    public static IReadOnlyList<string> NormalizeSet(IEnumerable<string> inputs)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var input in inputs)
        {
            if (!IsKnown(input))
            {
                continue;
            }

            var normalized = NormalizeId(input);
            if (seen.Add(normalized))
            {
                result.Add(normalized);
            }
        }

        result.Sort((left, right) => SortOrder(left).CompareTo(SortOrder(right)));
        return result;
    }
}
