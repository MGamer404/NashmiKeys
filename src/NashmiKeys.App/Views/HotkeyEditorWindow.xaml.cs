using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using NashmiKeys.App.Helpers;
using NashmiKeys.App.Services;
using NashmiKeys.Core.Configuration;
using NashmiKeys.Core.Input;
using MediaBrush = System.Windows.Media.Brush;

namespace NashmiKeys.App.Views;

public partial class HotkeyEditorWindow : Window
{
    private readonly Hotkey _hotkey;
    private readonly bool _isNew;
    private readonly HotkeyService _service;
    private readonly IReadOnlyList<Hotkey> _otherHotkeys;
    private readonly Func<Hotkey, bool, string?> _save;
    private readonly HashSet<string> _inputs = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _outputs = new();
    private readonly ObservableCollection<ChipItem> _chordItems = new();
    private readonly ObservableCollection<ChipItem> _outputItems = new();
    private readonly ObservableCollection<InputOption> _inputOptions = new();
    private readonly LowLevelKeyboardHook _keyboardHook = new();
    private readonly DispatcherTimer _inputTimer;
    private HashSet<string> _previousPressed = new(StringComparer.OrdinalIgnoreCase);
    private PadFamily _family = InputCatalog.DefaultFamily;
    private bool _outputCapturing;
    private bool _initialized;
    private bool _closing;
    private string _liveSignature = string.Empty;

    public HotkeyEditorWindow(
        Hotkey hotkey,
        bool isNew,
        HotkeyService service,
        IReadOnlyList<Hotkey> otherHotkeys,
        Func<Hotkey, bool, string?> save)
    {
        InitializeComponent();
        WindowTheme.Apply(this);

        _hotkey = hotkey;
        _isNew = isNew;
        _service = service;
        _otherHotkeys = otherHotkeys;
        _save = save;

        MaxHeight = Math.Max(560, SystemParameters.WorkArea.Height - 40);
        Height = Math.Min(780, MaxHeight);
        MaxWidth = Math.Max(700, SystemParameters.WorkArea.Width - 40);
        Width = Math.Min(820, MaxWidth);

        HeaderTitle.Text = isNew ? "Add hotkey" : "Edit hotkey";
        NameBox.Text = hotkey.Name;

        foreach (var input in InputCatalog.NormalizeSet(hotkey.Inputs))
        {
            _inputs.Add(input);
        }

        foreach (var output in KeyCatalog.NormalizeOutputs(hotkey.Outputs))
        {
            _outputs.Add(output);
        }

        ChordItems.ItemsSource = _chordItems;
        OutputItems.ItemsSource = _outputItems;
        ManualInputCombo.ItemsSource = _inputOptions;
        ManualOutputCombo.ItemsSource = KeyCatalog.All.Select(key => key.Name).ToList();

        _family = service.LatestSnapshot?.Family ?? InputCatalog.DefaultFamily;
        RebuildInputOptions();
        RefreshChord();
        RefreshOutputs();

        ModeCombo.ItemsSource = new[] { "Hold", "Tap", "Toggle" };
        SelectMode(hotkey.Mode);

        _keyboardHook.KeyCaptured += OnKeyCaptured;
        service.InputsPressed += OnInputsPressed;
        var baseline = service.LatestSnapshot?.Inputs ?? Array.Empty<string>();
        _previousPressed = new HashSet<string>(baseline, StringComparer.OrdinalIgnoreCase);

        _inputTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(25)
        };
        _inputTimer.Tick += (_, _) => RefreshLiveInputs();
        _inputTimer.Start();

        _initialized = true;
        UpdateModeExplanation();
        UpdateOverlapWarning();
        UpdateCaptureVisual();
        UpdateEmptyLabels();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        NameBox.Focus();
        NameBox.SelectAll();
    }

    private void RefreshLiveInputs()
    {
        if (_closing)
        {
            return;
        }

        var snapshot = _service.LatestSnapshot;
        var family = snapshot?.Family ?? InputCatalog.DefaultFamily;
        if (family != _family)
        {
            _family = family;
            RebuildInputOptions();
            RefreshChord();
        }

        var pressed = snapshot?.Inputs ?? Array.Empty<string>();
        var labels = pressed.Select(input => InputCatalog.Label(input, _family)).ToList();
        var signature = string.Join("|", labels);
        if (!string.Equals(signature, _liveSignature, StringComparison.Ordinal))
        {
            _liveSignature = signature;
            LiveInputItems.ItemsSource = labels;
            LiveInputEmpty.Visibility = labels.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        if (snapshot is null)
        {
            InputLiveTitle.Text = "Waiting for controller…";
            LiveInputEmpty.Text = "You can add inputs manually with the picker below.";
        }
        else if (snapshot.Pads.Count == 0)
        {
            InputLiveTitle.Text = "No controller detected — add inputs manually";
            LiveInputEmpty.Text = "Use the picker below to build the chord.";
        }
        else
        {
            InputLiveTitle.Text = "Press the buttons on your controller now";
            LiveInputEmpty.Text = "No buttons pressed";
        }

        foreach (var input in pressed)
        {
            if (!_previousPressed.Contains(input) && InputCatalog.IsKnown(input))
            {
                AddInput(input);
            }
        }

        _previousPressed = new HashSet<string>(pressed, StringComparer.OrdinalIgnoreCase);
    }

    private void OnInputsPressed(IReadOnlyList<string> inputs)
    {
        if (_closing)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (_closing)
            {
                return;
            }

            foreach (var input in inputs)
            {
                AddInput(input);
            }
        }, DispatcherPriority.Background);
    }

    private void AddInput(string inputId)
    {
        if (!InputCatalog.IsKnown(inputId) || !_inputs.Add(InputCatalog.NormalizeId(inputId)))
        {
            return;
        }

        RefreshChord();
        UpdateOverlapWarning();
    }

    private void RefreshChord()
    {
        _chordItems.Clear();
        foreach (var input in _inputs.OrderBy(InputCatalog.SortOrder))
        {
            _chordItems.Add(new ChipItem(input, InputCatalog.Label(input, _family)));
        }

        UpdateEmptyLabels();
    }

    private void RebuildInputOptions()
    {
        var selectedId = (ManualInputCombo.SelectedItem as InputOption)?.Id;
        _inputOptions.Clear();
        foreach (var definition in InputCatalog.All)
        {
            _inputOptions.Add(new InputOption(definition.Id, definition.Label(_family)));
        }

        if (selectedId is not null)
        {
            ManualInputCombo.SelectedItem = _inputOptions.FirstOrDefault(option =>
                string.Equals(option.Id, selectedId, StringComparison.OrdinalIgnoreCase));
        }
    }

    private void RemoveInput_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string inputId)
        {
            _inputs.Remove(inputId);
            RefreshChord();
            UpdateOverlapWarning();
        }
    }

    private void ClearInputs_Click(object sender, RoutedEventArgs e)
    {
        _inputs.Clear();
        RefreshChord();
        UpdateOverlapWarning();
    }

    private void AddManualInput_Click(object sender, RoutedEventArgs e)
    {
        if (ManualInputCombo.SelectedItem is InputOption option)
        {
            AddInput(option.Id);
            ManualInputCombo.SelectedIndex = -1;
        }
    }

    private void AddOutput(string keyName)
    {
        if (!KeyCatalog.TryGet(keyName, out var key))
        {
            return;
        }

        if (_outputs.Contains(key.Name, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        _outputs.Add(key.Name);
        RefreshOutputs();
        UpdateOverlapWarning();
    }

    private void RefreshOutputs()
    {
        _outputItems.Clear();
        foreach (var output in _outputs)
        {
            _outputItems.Add(new ChipItem(output, output));
        }

        UpdateEmptyLabels();
    }

    private void OnKeyCaptured(KeyDefinition key)
    {
        AddOutput(key.Name);
    }

    private void RemoveOutput_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string keyName)
        {
            _outputs.RemoveAll(name => string.Equals(name, keyName, StringComparison.OrdinalIgnoreCase));
            RefreshOutputs();
            UpdateOverlapWarning();
        }
    }

    private void ClearOutputs_Click(object sender, RoutedEventArgs e)
    {
        _outputs.Clear();
        RefreshOutputs();
        UpdateOverlapWarning();
    }

    private void AddManualOutput_Click(object sender, RoutedEventArgs e)
    {
        if (ManualOutputCombo.SelectedItem is string keyName)
        {
            AddOutput(keyName);
            ManualOutputCombo.SelectedIndex = -1;
        }
    }

    private void OutputCaptureBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        OutputCaptureBorder.Focus();
        e.Handled = true;
    }

    private void OutputCaptureBorder_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        StartOutputCapture();
    }

    private void OutputCaptureBorder_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        StopOutputCapture();
    }

    private void StartOutputCapture()
    {
        if (_outputCapturing)
        {
            return;
        }

        try
        {
            _keyboardHook.Install();
            _outputCapturing = true;
            ValidationText.Text = string.Empty;
            UpdateCaptureVisual();
        }
        catch (Exception exception)
        {
            ValidationText.Text = "Keyboard capture could not start: " + exception.Message;
        }
    }

    private void StopOutputCapture()
    {
        if (!_outputCapturing)
        {
            return;
        }

        _keyboardHook.Uninstall();
        _outputCapturing = false;
        UpdateCaptureVisual();
    }

    private void StopCapture_Click(object sender, RoutedEventArgs e)
    {
        StopOutputCapture();
        NameBox.Focus();
    }

    private void UpdateCaptureVisual()
    {
        if (_outputCapturing)
        {
            OutputCaptureBorder.BorderBrush = (MediaBrush)FindResource("AccentBrush");
            OutputCaptureBorder.Background = (MediaBrush)FindResource("AccentSoftBrush");
            OutputCaptureHint.Text = "Listening — press keys. Click Stop capturing when done.";
        }
        else
        {
            OutputCaptureBorder.BorderBrush = (MediaBrush)FindResource("BorderBrush");
            OutputCaptureBorder.Background = (MediaBrush)FindResource("SurfaceBrush");
            OutputCaptureHint.Text = "Click here, then press keyboard keys";
        }
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        StopOutputCapture();
    }

    private void SelectMode(HotkeyMode mode)
    {
        ModeCombo.SelectedIndex = mode switch
        {
            HotkeyMode.Tap => 1,
            HotkeyMode.Toggle => 2,
            _ => 0
        };
    }

    private HotkeyMode ReadMode()
    {
        return ModeCombo.SelectedIndex switch
        {
            1 => HotkeyMode.Tap,
            2 => HotkeyMode.Toggle,
            _ => HotkeyMode.Hold
        };
    }

    private void ModeCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_initialized)
        {
            UpdateModeExplanation();
        }
    }

    private void UpdateModeExplanation()
    {
        ModeExplanation.Text = ReadMode() switch
        {
            HotkeyMode.Tap =>
                "Presses all outputs once for about 40 ms, then releases them. It cannot fire again until the whole chord is released.",
            HotkeyMode.Toggle =>
                "The first activation presses and holds the outputs; the next activation releases them. The chord must be released between toggles.",
            _ =>
                "Outputs stay physically held while every chord input is held. Releasing any input releases all outputs in reverse order."
        };
    }

    private void UpdateOverlapWarning()
    {
        if (_inputs.Count == 0)
        {
            OverlapBanner.Visibility = Visibility.Collapsed;
            OverlapText.Text = string.Empty;
            return;
        }

        var messages = new List<string>();
        foreach (var other in _otherHotkeys)
        {
            if (!other.Enabled || other.Inputs.Count == 0)
            {
                continue;
            }

            var otherSet = new HashSet<string>(other.Inputs, StringComparer.OrdinalIgnoreCase);
            var name = string.IsNullOrWhiteSpace(other.Name) ? "Untitled hotkey" : other.Name.Trim();
            if (otherSet.SetEquals(_inputs))
            {
                messages.Add($"\"{name}\" has an identical input set.");
            }
            else if (otherSet.IsSubsetOf(_inputs))
            {
                messages.Add($"\"{name}\" uses a subset of these inputs.");
            }
            else if (_inputs.IsSubsetOf(otherSet))
            {
                messages.Add($"\"{name}\" uses a superset of these inputs.");
            }
        }

        if (messages.Count == 0)
        {
            OverlapBanner.Visibility = Visibility.Collapsed;
            OverlapText.Text = string.Empty;
            return;
        }

        OverlapBanner.Visibility = Visibility.Visible;
        OverlapText.Text = string.Join(" ", messages) +
                           " Both can match; the larger chord wins, and equal chords use list order.";
    }

    private void UpdateEmptyLabels()
    {
        ChordEmpty.Visibility = _chordItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        OutputEmpty.Visibility = _outputItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        StopOutputCapture();

        if (_inputs.Count == 0)
        {
            ValidationText.Text = "Add at least one controller input before saving.";
            return;
        }

        if (_outputs.Count == 0)
        {
            ValidationText.Text = "Add at least one keyboard output key before saving.";
            return;
        }

        var edited = _hotkey.Clone();
        edited.Name = string.IsNullOrWhiteSpace(NameBox.Text) ? "Hotkey" : NameBox.Text.Trim();
        edited.Mode = ReadMode();
        edited.Inputs = _inputs.OrderBy(InputCatalog.SortOrder).ToList();
        edited.Outputs = _outputs.ToList();

        var error = _save(edited, _isNew);
        if (error is not null)
        {
            ValidationText.Text = "Could not save: " + error;
            return;
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        _closing = true;
        _inputTimer.Stop();
        _service.InputsPressed -= OnInputsPressed;
        _keyboardHook.Dispose();
        _outputCapturing = false;
    }

    private sealed class ChipItem
    {
        public ChipItem(string id, string label)
        {
            Id = id;
            Label = label;
        }

        public string Id { get; }
        public string Label { get; }
    }

    private sealed class InputOption
    {
        public InputOption(string id, string label)
        {
            Id = id;
            Label = label;
        }

        public string Id { get; }
        public string Label { get; }

        public override string ToString()
        {
            return Label;
        }
    }
}
