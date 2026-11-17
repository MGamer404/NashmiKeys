using System.Text;
using System.Xml.Linq;
using NashmiKeys.Core.Configuration;
using NashmiKeys.Core.Engine;
using NashmiKeys.Core.Injection;
using NashmiKeys.Core.Input;
using NashmiKeys.Core.Logging;
using NashmiKeys.Core.Startup;

namespace NashmiKeys.Core.Diagnostics;

/// <summary>
/// Plain-assert self test suite shared by the WPF app (--selftest), the CLI
/// (selftest) and tests/NashmiKeys.Tests so all three run identical checks.
/// </summary>
public static class SelfTestSuite
{
    public static int Run(TextWriter output, ILogger? logger = null)
    {
        var runner = new Runner(output, logger);
        runner.Test("config save/load roundtrip (non-ASCII name)", ConfigRoundTrip);
        runner.Test("config missing fields get defaults", ConfigMissingFields);
        runner.Test("corrupt config falls back to defaults", CorruptConfigFallsBack);
        runner.Test("Hold activates once and releases on any input release", HoldBehavior);
        runner.Test("subset precedence: larger chord wins, smaller takes over", SubsetPrecedence);
        runner.Test("chord input order is irrelevant", ChordOrderFree);
        runner.Test("Tap fires once per hold, releases after ~40 ms, re-arms", TapBehavior);
        runner.Test("Tap early release releases outputs immediately", TapEarlyRelease);
        runner.Test("Toggle flips on/off and requires chord release", ToggleBehavior);
        runner.Test("pause releases every held output and ignores input", PauseReleasesAll);
        runner.Test("injection order: modifiers first, reverse on release", InjectionOrder);
        runner.Test("key catalog values", KeyCatalogValues);
        runner.Test("scheduled-task XML is well formed and correct", ScheduledTaskXml);
        runner.Test("stick hysteresis does not chatter at the threshold", StickHysteresis);
        runner.Test("comboHoldMs waits for a continuous hold", ComboHoldDelay);
        runner.Test("disabled hotkeys are ignored", DisabledIgnored);
        runner.Test("duplicate outputs are pressed once", DuplicateOutputs);
        runner.Test("Tap lock blocks other chords until chord release", TapLockBlocksOther);
        runner.Test("Toggle pause releases held outputs", TogglePauseRelease);
        return runner.Finish();
    }

    private static void ConfigRoundTrip()
    {
        var directory = TempDirectory();
        try
        {
            var store = new ConfigStore(directory);
            var config = AppConfig.CreateDefault();
            config.Settings.StartWithWindows = true;
            config.Settings.StartMinimized = true;
            config.Settings.Paused = true;
            config.Settings.ComboHoldMs = 250;
            config.Settings.MinimizeToTray = false;
            config.Settings.L2Threshold = 0.6;
            config.Settings.R2Threshold = 0.55;
            config.Hotkeys[0].Name = "视窗键 — Тест Ω";
            config.Hotkeys[0].Inputs = new List<string> { "l3", "r3" };
            config.Hotkeys[0].Outputs = new List<string> { "Win" };
            config.Hotkeys[0].Mode = HotkeyMode.Toggle;

            store.Save(config);
            var loaded = store.Load();

            Assert(loaded.Error is null, "load reported an error: " + loaded.Error);
            AssertEqual(1, loaded.Config.Version);
            AssertEqual(2, loaded.Config.Hotkeys.Count);
            AssertEqual("视窗键 — Тест Ω", loaded.Config.Hotkeys[0].Name);
            AssertEqual(HotkeyMode.Toggle, loaded.Config.Hotkeys[0].Mode);
            AssertEqual(true, loaded.Config.Settings.StartWithWindows);
            AssertEqual(true, loaded.Config.Settings.StartMinimized);
            AssertEqual(true, loaded.Config.Settings.Paused);
            AssertEqual(250, loaded.Config.Settings.ComboHoldMs);
            AssertEqual(false, loaded.Config.Settings.MinimizeToTray);
            AssertAlmostEqual(0.6, loaded.Config.Settings.L2Threshold);
            AssertAlmostEqual(0.55, loaded.Config.Settings.R2Threshold);

            var json = File.ReadAllText(store.FilePath);
            Assert(json.Contains("视窗键", StringComparison.Ordinal), "non-ASCII name was escaped in JSON.");
            Assert(json.Contains("\"mode\": \"Toggle\"", StringComparison.Ordinal), "mode was not written as a readable string.");
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    private static void ConfigMissingFields()
    {
        var directory = TempDirectory();
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "config.json");
            File.WriteAllText(path, """
                {
                  "version": 1,
                  "somethingUnknown": true,
                  "hotkeys": [
                    { "name": "Minimal", "inputs": ["start"], "outputs": ["Alt", "Tab"], "extra": 42 }
                  ]
                }
                """);

            var store = new ConfigStore(directory);
            var loaded = store.Load();
            Assert(loaded.Error is null, "unexpected error: " + loaded.Error);
            AssertEqual(1, loaded.Config.Hotkeys.Count);
            var hotkey = loaded.Config.Hotkeys[0];
            Assert(!string.IsNullOrWhiteSpace(hotkey.Id), "missing id was not generated.");
            AssertEqual(true, hotkey.Enabled);
            AssertEqual(HotkeyMode.Hold, hotkey.Mode);
            AssertSequence(hotkey.Inputs, "start");
            AssertSequence(hotkey.Outputs, "Alt", "Tab");
            AssertEqual(0, loaded.Config.Settings.ComboHoldMs);
            AssertEqual(true, loaded.Config.Settings.MinimizeToTray);
            AssertAlmostEqual(0.5, loaded.Config.Settings.L2Threshold);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    private static void CorruptConfigFallsBack()
    {
        var directory = TempDirectory();
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "config.json");
            File.WriteAllText(path, "{ this is not json ");

            var store = new ConfigStore(directory);
            var loaded = store.Load();
            Assert(loaded.Error is not null, "corrupt config did not surface an error.");
            AssertEqual(2, loaded.Config.Hotkeys.Count);
            Assert(loaded.BackupPath is not null && File.Exists(loaded.BackupPath), "invalid config was not backed up.");
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    private static void HoldBehavior()
    {
        var injector = new FakeInjector();
        var hotkey = HotkeyOf("Windows key", HotkeyMode.Hold, new[] { "l3", "r3" }, new[] { "Win" });
        var engine = new HotkeyEngine(new[] { hotkey }, injector);

        var events = engine.Tick(Pressed("l3", "r3"), 0);
        AssertSequence(injector.Log, "+Win");
        Assert(events.Any(engineEvent => engineEvent.Kind == EngineEventKind.Activated), "no activation event.");

        engine.Tick(Pressed("l3", "r3"), 8);
        engine.Tick(Pressed("l3", "r3"), 16);
        AssertSequence(injector.Log, "+Win");

        events = engine.Tick(Pressed("l3"), 24);
        AssertSequence(injector.Log, "+Win", "-Win");
        Assert(events.Any(engineEvent => engineEvent.Kind == EngineEventKind.Deactivated), "no deactivation event.");
        Assert(!engine.IsActive, "engine still active after release.");
    }

    private static void SubsetPrecedence()
    {
        var injector = new FakeInjector();
        var small = HotkeyOf("Small", HotkeyMode.Hold, new[] { "l3", "r3" }, new[] { "F1" });
        var large = HotkeyOf("Large", HotkeyMode.Hold, new[] { "l3", "r3", "start" }, new[] { "F2" });
        var engine = new HotkeyEngine(new[] { small, large }, injector);

        engine.Tick(Pressed("l3", "r3", "start"), 0);
        AssertSequence(injector.Log, "+F2");
        AssertEqual(large.Id, engine.ActiveHotkeyId);

        engine.Tick(Pressed("l3", "r3"), 8);
        AssertSequence(injector.Log, "+F2", "-F2");

        engine.Tick(Pressed("l3", "r3"), 16);
        AssertSequence(injector.Log, "+F2", "-F2", "+F1");
    }

    private static void ChordOrderFree()
    {
        var injector = new FakeInjector();
        var hotkey = HotkeyOf("Reversed", HotkeyMode.Hold, new[] { "r3", "l3" }, new[] { "Win" });
        var engine = new HotkeyEngine(new[] { hotkey }, injector);
        engine.Tick(Pressed("l3", "r3"), 0);
        AssertSequence(injector.Log, "+Win");
    }

    private static void TapBehavior()
    {
        var injector = new FakeInjector();
        var hotkey = HotkeyOf("Tap key", HotkeyMode.Tap, new[] { "l3", "r3" }, new[] { "Win" });
        var engine = new HotkeyEngine(new[] { hotkey }, injector);

        engine.Tick(Pressed("l3", "r3"), 0);
        AssertSequence(injector.Log, "+Win");

        engine.Tick(Pressed("l3", "r3"), 20);
        AssertSequence(injector.Log, "+Win");

        engine.Tick(Pressed("l3", "r3"), 40);
        AssertSequence(injector.Log, "+Win", "-Win");

        engine.Tick(Pressed("l3", "r3"), 80);
        AssertSequence(injector.Log, "+Win", "-Win");

        engine.Tick(Pressed(), 90);
        engine.Tick(Pressed("l3", "r3"), 100);
        AssertSequence(injector.Log, "+Win", "-Win", "+Win");
    }

    private static void TapEarlyRelease()
    {
        var injector = new FakeInjector();
        var hotkey = HotkeyOf("Tap key", HotkeyMode.Tap, new[] { "l3", "r3" }, new[] { "Win" });
        var engine = new HotkeyEngine(new[] { hotkey }, injector);

        engine.Tick(Pressed("l3", "r3"), 0);
        engine.Tick(Pressed("l3"), 10);
        AssertSequence(injector.Log, "+Win", "-Win");
        Assert(!engine.IsActive, "tap stayed active after chord broke.");
    }

    private static void ToggleBehavior()
    {
        var injector = new FakeInjector();
        var hotkey = HotkeyOf("Task switcher", HotkeyMode.Toggle, new[] { "start", "back" }, new[] { "Alt", "Tab" });
        var engine = new HotkeyEngine(new[] { hotkey }, injector);

        engine.Tick(Pressed("start", "back"), 0);
        AssertSequence(injector.Log, "+Alt", "+Tab");

        engine.Tick(Pressed(), 10);
        AssertSequence(injector.Log, "+Alt", "+Tab");

        engine.Tick(Pressed("start", "back"), 20);
        AssertSequence(injector.Log, "+Alt", "+Tab", "-Tab", "-Alt");

        engine.Tick(Pressed("start", "back"), 30);
        AssertSequence(injector.Log, "+Alt", "+Tab", "-Tab", "-Alt");

        engine.Tick(Pressed(), 40);
        engine.Tick(Pressed("start", "back"), 50);
        AssertSequence(injector.Log, "+Alt", "+Tab", "-Tab", "-Alt", "+Alt", "+Tab");
    }

    private static void PauseReleasesAll()
    {
        var injector = new FakeInjector();
        var hotkey = HotkeyOf("Windows key", HotkeyMode.Hold, new[] { "l3", "r3" }, new[] { "Win" });
        var engine = new HotkeyEngine(new[] { hotkey }, injector);
        engine.Tick(Pressed("l3", "r3"), 0);
        AssertSequence(injector.Log, "+Win");

        engine.SetPaused(true);
        AssertSequence(injector.Log, "+Win", "-Win");
        Assert(engine.IsPaused, "engine did not pause.");

        engine.Tick(Pressed("l3", "r3"), 8);
        AssertSequence(injector.Log, "+Win", "-Win");
        AssertEqual(0, engine.HeldOutputKeys.Count);
    }

    private static void InjectionOrder()
    {
        var injector = new FakeInjector();
        var hotkey = HotkeyOf(
            "Order test",
            HotkeyMode.Hold,
            new[] { "l3", "r3" },
            new[] { "Tab", "Shift", "Win", "Alt", "Ctrl", "A" });
        var engine = new HotkeyEngine(new[] { hotkey }, injector);

        engine.Tick(Pressed("l3", "r3"), 0);
        AssertSequence(injector.Log, "+Win", "+Ctrl", "+Alt", "+Shift", "+Tab", "+A");

        engine.Tick(Pressed(), 8);
        AssertSequence(injector.Log, "+Win", "+Ctrl", "+Alt", "+Shift", "+Tab", "+A",
            "-A", "-Tab", "-Shift", "-Alt", "-Ctrl", "-Win");
    }

    private static void KeyCatalogValues()
    {
        AssertTrue(KeyCatalog.TryGet("Win", out var win), "Win missing.");
        AssertEqual((ushort)0x5B, win.VirtualKey);
        AssertEqual((ushort)0x5B, win.ScanCode);
        AssertEqual(true, win.Extended);

        AssertTrue(KeyCatalog.TryGet("Alt", out var alt), "Alt missing.");
        AssertEqual((ushort)0x12, alt.VirtualKey);

        AssertTrue(KeyCatalog.TryGet("Tab", out var tab), "Tab missing.");
        AssertEqual((ushort)0x09, tab.VirtualKey);

        AssertTrue(KeyCatalog.TryGet("F5", out var f5), "F5 missing.");
        AssertEqual((ushort)0x74, f5.VirtualKey);

        AssertTrue(KeyCatalog.TryGet("A", out var a), "A missing.");
        AssertEqual((ushort)0x41, a.VirtualKey);

        AssertTrue(KeyCatalog.TryFromKeyboard(0xA0, 0x2A, false, out var shift), "left shift capture failed.");
        AssertEqual("Shift", shift.Name);
        AssertTrue(KeyCatalog.TryFromKeyboard(0x0D, 0x1C, true, out var numEnter), "numpad enter capture failed.");
        AssertEqual("NumEnter", numEnter.Name);

        AssertEqual(110, KeyCatalog.All.Count);
    }

    private static void ScheduledTaskXml()
    {
        const string exePath = @"C:\Program Files\NashmiKeys & Co\NashmiKeys.exe";
        const string user = @"CONTOSO\User & Co";
        var xml = StartupManager.BuildTaskXml(exePath, user, startMinimized: true);
        var document = XDocument.Parse(xml);
        var ns = (XNamespace)"http://schemas.microsoft.com/windows/2004/02/mit/task";

        AssertEqual("1.2", document.Root!.Attribute("version")!.Value);
        AssertEqual("HighestAvailable", document.Descendants(ns + "RunLevel").Single().Value);
        AssertEqual("\"" + exePath + "\"", document.Descendants(ns + "Command").Single().Value);
        AssertEqual("--minimized", document.Descendants(ns + "Arguments").Single().Value);
        AssertEqual("IgnoreNew", document.Descendants(ns + "MultipleInstancesPolicy").Single().Value);
        AssertEqual("false", document.Descendants(ns + "DisallowStartIfOnBatteries").Single().Value);
        AssertEqual("false", document.Descendants(ns + "StopIfGoingOnBatteries").Single().Value);
        AssertEqual("PT0S", document.Descendants(ns + "ExecutionTimeLimit").Single().Value);
        AssertEqual(user, document.Descendants(ns + "LogonTrigger").Single().Element(ns + "UserId")!.Value);
        AssertEqual("InteractiveToken", document.Descendants(ns + "LogonType").Single().Value);

        var withoutMinimized = XDocument.Parse(StartupManager.BuildTaskXml(exePath, user, startMinimized: false));
        Assert(!withoutMinimized.Descendants(ns + "Arguments").Any(), "arguments should be omitted when not minimized.");
    }

    private static void StickHysteresis()
    {
        var gate = new AxisHysteresis(0.75, 0.65);
        var values = new[] { 0.74, 0.76, 0.74, 0.70, 0.66, 0.649, 0.74, 0.76, 0.0 };
        var expected = new[] { false, true, true, true, true, false, false, true, false };
        for (var index = 0; index < values.Length; index++)
        {
            var actual = gate.Update(values[index]);
            AssertEqual(expected[index], actual);
        }
    }

    private static void ComboHoldDelay()
    {
        var injector = new FakeInjector();
        var hotkey = HotkeyOf("Delayed", HotkeyMode.Hold, new[] { "l3", "r3" }, new[] { "F1" });
        var engine = new HotkeyEngine(new[] { hotkey }, injector, comboHoldMs: 250);

        engine.Tick(Pressed("l3", "r3"), 0);
        engine.Tick(Pressed("l3", "r3"), 100);
        engine.Tick(Pressed("l3", "r3"), 249);
        AssertEqual(0, injector.Log.Count);

        engine.Tick(Pressed(), 260);
        AssertEqual(0, injector.Log.Count);

        engine.Tick(Pressed("l3", "r3"), 300);
        engine.Tick(Pressed("l3", "r3"), 549);
        AssertEqual(0, injector.Log.Count);

        engine.Tick(Pressed("l3", "r3"), 550);
        AssertSequence(injector.Log, "+F1");
    }

    private static void DisabledIgnored()
    {
        var injector = new FakeInjector();
        var hotkey = HotkeyOf("Disabled", HotkeyMode.Hold, new[] { "l3", "r3" }, new[] { "Win" }, enabled: false);
        var engine = new HotkeyEngine(new[] { hotkey }, injector);
        engine.Tick(Pressed("l3", "r3"), 0);
        AssertEqual(0, injector.Log.Count);
    }

    private static void DuplicateOutputs()
    {
        var injector = new FakeInjector();
        var hotkey = HotkeyOf("Duplicates", HotkeyMode.Hold, new[] { "l3", "r3" }, new[] { "Ctrl", "Ctrl", "A" });
        var engine = new HotkeyEngine(new[] { hotkey }, injector);
        engine.Tick(Pressed("l3", "r3"), 0);
        AssertSequence(injector.Log, "+Ctrl", "+A");
        engine.Tick(Pressed(), 8);
        AssertSequence(injector.Log, "+Ctrl", "+A", "-A", "-Ctrl");
    }

    private static void TapLockBlocksOther()
    {
        var injector = new FakeInjector();
        var tap = HotkeyOf("Tap", HotkeyMode.Tap, new[] { "l3", "r3" }, new[] { "Win" });
        var hold = HotkeyOf("Hold", HotkeyMode.Hold, new[] { "l3" }, new[] { "F1" });
        var engine = new HotkeyEngine(new[] { tap, hold }, injector);

        engine.Tick(Pressed("l3", "r3"), 0);
        engine.Tick(Pressed("l3", "r3"), 40);
        AssertSequence(injector.Log, "+Win", "-Win");

        engine.Tick(Pressed("l3"), 50);
        AssertSequence(injector.Log, "+Win", "-Win");

        engine.Tick(Pressed(), 60);
        engine.Tick(Pressed("l3"), 70);
        AssertSequence(injector.Log, "+Win", "-Win", "+F1");
    }

    private static void TogglePauseRelease()
    {
        var injector = new FakeInjector();
        var hotkey = HotkeyOf("Toggle", HotkeyMode.Toggle, new[] { "start", "back" }, new[] { "Ctrl", "Shift" });
        var engine = new HotkeyEngine(new[] { hotkey }, injector);
        engine.Tick(Pressed("start", "back"), 0);
        AssertSequence(injector.Log, "+Ctrl", "+Shift");
        engine.SetPaused(true);
        AssertSequence(injector.Log, "+Ctrl", "+Shift", "-Shift", "-Ctrl");
        AssertEqual(0, engine.HeldOutputKeys.Count);
    }

    private static Hotkey HotkeyOf(
        string name,
        HotkeyMode mode,
        string[] inputs,
        string[] outputs,
        bool enabled = true)
    {
        return new Hotkey
        {
            Id = Guid.NewGuid().ToString("D"),
            Name = name,
            Mode = mode,
            Enabled = enabled,
            Inputs = inputs.ToList(),
            Outputs = outputs.ToList()
        };
    }

    private static HashSet<string> Pressed(params string[] inputs)
    {
        return new HashSet<string>(inputs, StringComparer.OrdinalIgnoreCase);
    }

    private static string TempDirectory()
    {
        return Path.Combine(Path.GetTempPath(), "NashmiKeys-selftest-" + Guid.NewGuid().ToString("N"));
    }

    private static void DeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
            // Best effort.
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new SelfTestFailure(message);
        }
    }

    private static void AssertTrue(bool condition, string message) => Assert(condition, message);

    private static void AssertEqual<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new SelfTestFailure($"Expected '{expected}' but got '{actual}'.");
        }
    }

    private static void AssertAlmostEqual(double expected, double actual)
    {
        if (Math.Abs(expected - actual) > 0.0001)
        {
            throw new SelfTestFailure($"Expected '{expected}' but got '{actual}'.");
        }
    }

    private static void AssertSequence(IReadOnlyList<string> actual, params string[] expected)
    {
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
        {
            throw new SelfTestFailure(
                $"Expected [{string.Join(", ", expected)}] but got [{string.Join(", ", actual)}].");
        }
    }

    private sealed class FakeInjector : IKeyInjector
    {
        public List<string> Log { get; } = new();

        public void KeyDown(KeyDefinition key)
        {
            Log.Add("+" + key.Name);
        }

        public void KeyUp(KeyDefinition key)
        {
            Log.Add("-" + key.Name);
        }
    }

    private sealed class SelfTestFailure : Exception
    {
        public SelfTestFailure(string message) : base(message)
        {
        }
    }

    private sealed class Runner
    {
        private readonly TextWriter _output;
        private readonly ILogger _logger;
        private int _passed;
        private int _failed;

        public Runner(TextWriter output, ILogger? logger)
        {
            _output = output;
            _logger = logger ?? NullLogger.Instance;
        }

        public void Test(string name, Action test)
        {
            try
            {
                test();
                _passed++;
                WriteLine("[PASS] " + name);
            }
            catch (Exception exception)
            {
                _failed++;
                WriteLine("[FAIL] " + name + ": " + exception.Message);
                _logger.Error("Self test failed: " + name, exception);
            }
        }

        public int Finish()
        {
            WriteLine($"Self test result: {_passed} passed, {_failed} failed.");
            WriteLine(_failed == 0 ? "SELFTEST OK" : "SELFTEST FAILED");
            return _failed == 0 ? 0 : 1;
        }

        private void WriteLine(string line)
        {
            _output.WriteLine(line);
            _logger.Info(line);
        }
    }
}
