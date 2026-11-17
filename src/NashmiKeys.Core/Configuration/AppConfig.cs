using System.Text.Json.Serialization;

namespace NashmiKeys.Core.Configuration;

public enum HotkeyMode
{
    Hold = 0,
    Tap = 1,
    Toggle = 2
}

public sealed class AppConfig
{
    public int Version { get; set; } = 1;

    public AppSettings Settings { get; set; } = new();

    public List<Hotkey> Hotkeys { get; set; } = new();

    public static AppConfig CreateDefault()
    {
        return new AppConfig
        {
            Version = 1,
            Settings = new AppSettings(),
            Hotkeys = new List<Hotkey>
            {
                new()
                {
                    Id = Guid.NewGuid().ToString("D"),
                    Name = "Windows key",
                    Enabled = true,
                    Mode = HotkeyMode.Hold,
                    Inputs = new List<string> { "l3", "r3" },
                    Outputs = new List<string> { "Win" }
                },
                new()
                {
                    Id = Guid.NewGuid().ToString("D"),
                    Name = "Task switcher",
                    Enabled = true,
                    Mode = HotkeyMode.Hold,
                    Inputs = new List<string> { "start", "back" },
                    Outputs = new List<string> { "Alt", "Tab" }
                }
            }
        };
    }
}

public sealed class AppSettings
{
    public bool StartWithWindows { get; set; }

    public bool StartMinimized { get; set; }

    public bool Paused { get; set; }

    public int ComboHoldMs { get; set; }

    public bool MinimizeToTray { get; set; } = true;

    public double L2Threshold { get; set; } = 0.5;

    public double R2Threshold { get; set; } = 0.5;
}

public sealed class Hotkey
{
    public string Id { get; set; } = Guid.NewGuid().ToString("D");

    public string Name { get; set; } = "Hotkey";

    public bool Enabled { get; set; } = true;

    [JsonConverter(typeof(TolerantHotkeyModeConverter))]
    public HotkeyMode Mode { get; set; } = HotkeyMode.Hold;

    public List<string> Inputs { get; set; } = new();

    public List<string> Outputs { get; set; } = new();

    public Hotkey Clone()
    {
        return new Hotkey
        {
            Id = Id,
            Name = Name,
            Enabled = Enabled,
            Mode = Mode,
            Inputs = new List<string>(Inputs),
            Outputs = new List<string>(Outputs)
        };
    }

    public Hotkey CloneWithNewId()
    {
        var clone = Clone();
        clone.Id = Guid.NewGuid().ToString("D");
        return clone;
    }

    public HashSet<string> InputSet()
    {
        return new HashSet<string>(Inputs, StringComparer.OrdinalIgnoreCase);
    }
}
