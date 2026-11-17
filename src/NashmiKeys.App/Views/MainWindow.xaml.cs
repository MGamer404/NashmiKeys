using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NashmiKeys.App.Helpers;
using NashmiKeys.App.Services;
using NashmiKeys.App.ViewModels;
using NashmiKeys.Core;
using NashmiKeys.Core.Configuration;
using NashmiKeys.Core.Engine;
using NashmiKeys.Core.Input;
using NashmiKeys.Core.Logging;
using NashmiKeys.Core.Sdl;
using NashmiKeys.Core.Startup;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using MediaBrush = System.Windows.Media.Brush;

namespace NashmiKeys.App.Views;

public partial class MainWindow : Window
{
    private readonly AppConfig _config;
    private readonly ConfigStore _store;
    private readonly HotkeyService _service;
    private readonly StartupManager _startup;
    private readonly ILogger _logger;
    private readonly string? _configError;
    private readonly bool _elevated;
    private readonly ObservableCollection<HotkeyCardViewModel> _cards = new();
    private readonly DispatcherTimer _uiTimer;
    private Forms.NotifyIcon? _tray;
    private Drawing.Icon? _trayImage;
    private Forms.ToolStripMenuItem? _trayPauseItem;
    private Forms.ToolStripMenuItem? _trayStartupItem;
    private bool _pauseUiUpdating;
    private bool _cardCallbackSuppressed;
    private bool _allowClose;
    private bool _exitRequested;
    private bool _balloonShown;
    private string _lastPressedSignature = string.Empty;
    private string? _lastSdlError;
    private PadFamily _family = InputCatalog.DefaultFamily;

    public MainWindow(
        AppConfig config,
        ConfigStore store,
        HotkeyService service,
        StartupManager startup,
        ILogger logger,
        string? configError,
        bool elevated)
    {
        InitializeComponent();

        _config = config;
        _store = store;
        _service = service;
        _startup = startup;
        _logger = logger;
        _configError = configError;
        _elevated = elevated;

        Icon = IconFactory.CreateWindowIcon();
        WindowTheme.Apply(this);

        HotkeyItems.ItemsSource = _cards;
        _uiTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _uiTimer.Tick += (_, _) => RefreshFromController();
        _uiTimer.Start();

        _service.HotkeyEvent += OnHotkeyEvent;

        BuildCards();
        RebuildBanners();
        UpdatePauseUi();
        CreateTray();
    }

    public event Action? ExitRequested;

    private void CreateTray()
    {
        _trayImage = IconFactory.CreateTrayIcon();
        var menu = new Forms.ContextMenuStrip
        {
            Renderer = new DarkMenuRenderer(),
            ShowImageMargin = true,
            ShowCheckMargin = true,
            BackColor = Drawing.Color.FromArgb(0x12, 0x16, 0x1E),
            ForeColor = Drawing.Color.FromArgb(0xE8, 0xEC, 0xF3),
            Padding = new Forms.Padding(4)
        };

        var openItem = new Forms.ToolStripMenuItem("Open NashmiKeys")
        {
            Font = new Drawing.Font(menu.Font, Drawing.FontStyle.Bold)
        };
        openItem.Click += (_, _) => ShowFromTray();

        _trayPauseItem = new Forms.ToolStripMenuItem("Pause hotkeys") { CheckOnClick = false };
        _trayPauseItem.Click += (_, _) => TogglePauseFromTray();

        _trayStartupItem = new Forms.ToolStripMenuItem("Start with Windows") { CheckOnClick = false };
        _trayStartupItem.Click += (_, _) => ToggleStartWithWindowsFromTray();

        var exitItem = new Forms.ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => RequestExit();

        menu.Items.Add(openItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(_trayPauseItem);
        menu.Items.Add(_trayStartupItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(exitItem);

        _tray = new Forms.NotifyIcon
        {
            Icon = _trayImage,
            Text = "NashmiKeys",
            ContextMenuStrip = menu,
            Visible = true
        };
        _tray.DoubleClick += (_, _) => ShowFromTray();

        UpdateTrayChecks();
    }

    private void BuildCards()
    {
        _family = _service.LatestSnapshot?.Family ?? InputCatalog.DefaultFamily;
        _cards.Clear();
        foreach (var hotkey in _config.Hotkeys)
        {
            _cards.Add(new HotkeyCardViewModel(hotkey, _family, OnCardEnabledChanged));
        }

        UpdateEmptyState();
    }

    private void OnCardEnabledChanged(HotkeyCardViewModel card)
    {
        if (_cardCallbackSuppressed)
        {
            return;
        }

        var previous = !card.Model.Enabled;
        var error = PersistConfig(updateEngine: true);
        if (error is not null)
        {
            _cardCallbackSuppressed = true;
            card.Enabled = previous;
            card.RefreshEnabled();
            _cardCallbackSuppressed = false;
            ShowError("Could not save the hotkey change", error);
        }

        UpdateEmptyState();
    }

    private void UpdateEmptyState()
    {
        var hasHotkeys = _cards.Count > 0;
        EmptyState.Visibility = hasHotkeys ? Visibility.Collapsed : Visibility.Visible;
        HotkeyItems.Visibility = hasHotkeys ? Visibility.Visible : Visibility.Collapsed;

        var enabled = _config.Hotkeys.Count(hotkey => hotkey.Enabled);
        HotkeyCountText.Text = _config.Hotkeys.Count == 0
            ? string.Empty
            : $"{enabled} of {_config.Hotkeys.Count} enabled";
    }

    private void RefreshFromController()
    {
        if (!IsVisible)
        {
            return;
        }

        var snapshot = _service.LatestSnapshot;
        if (snapshot is not null)
        {
            if (snapshot.Family != _family)
            {
                _family = snapshot.Family;
                foreach (var card in _cards)
                {
                    card.RefreshChips(_family);
                }
            }

            UpdateControllerPill(snapshot);
            UpdatePressedStrip(snapshot);
        }
        else
        {
            ControllerStatusText.Text = _service.LastSdlError is null ? "Starting controller…" : "Controller unavailable";
            BatteryText.Text = string.Empty;
            ControllerDot.Fill = (MediaBrush)FindResource("FaintBrush");
            PressedItems.ItemsSource = null;
            PressedNoneText.Visibility = Visibility.Visible;
        }

        var sdlError = _service.LastSdlError;
        if (!string.Equals(sdlError, _lastSdlError, StringComparison.Ordinal))
        {
            _lastSdlError = sdlError;
            RebuildBanners();
        }
    }

    private void UpdateControllerPill(ControllerSnapshot snapshot)
    {
        MediaBrush dotBrush;
        string text;
        if (snapshot.Pads.Count == 0)
        {
            dotBrush = (MediaBrush)FindResource(snapshot.DetectedCount > 0 ? "WarningBrush" : "FaintBrush");
            text = snapshot.DetectedCount > 0 ? "Controller unavailable" : "No controller";
        }
        else if (snapshot.Pads.Count == 1)
        {
            dotBrush = (MediaBrush)FindResource("SuccessBrush");
            text = ShortPadName(snapshot.Pads[0]) + " connected";
        }
        else
        {
            dotBrush = (MediaBrush)FindResource("SuccessBrush");
            text = $"{snapshot.Pads.Count} controllers";
        }

        ControllerDot.Fill = dotBrush;
        ControllerStatusText.Text = text;

        var battery = snapshot.BatteryPercent;
        BatteryText.Text = battery is null ? string.Empty : $"{battery}%";
        BatteryText.Foreground = battery switch
        {
            < 20 => (MediaBrush)FindResource("DangerBrush"),
            < 40 => (MediaBrush)FindResource("WarningBrush"),
            _ => (MediaBrush)FindResource("MutedBrush")
        };

        var tooltip = new System.Text.StringBuilder();
        foreach (var pad in snapshot.Pads)
        {
            tooltip.AppendLine($"{pad.Name} — {pad.SdlTypeName}");
            var batteryText = pad.BatteryPercent is null ? "battery unavailable" : $"{pad.BatteryPercent}% ({pad.PowerState})";
            tooltip.AppendLine("  " + batteryText);
        }

        if (snapshot.DetectedCount > snapshot.Pads.Count)
        {
            tooltip.AppendLine($"{snapshot.DetectedCount - snapshot.Pads.Count} detected controller(s) could not be opened. Check the log.");
        }

        tooltip.Append("SDL ").Append(snapshot.SdlVersion);
        ControllerPill.ToolTip = tooltip.ToString().TrimEnd();
    }

    private static string ShortPadName(PadInfo pad)
    {
        var name = pad.Name;
        if (name.Contains("DualSense", StringComparison.OrdinalIgnoreCase))
        {
            return "DualSense";
        }

        if (name.Contains("DualShock", StringComparison.OrdinalIgnoreCase))
        {
            return "DualShock";
        }

        if (name.Contains("Switch", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Nintendo", StringComparison.OrdinalIgnoreCase))
        {
            return "Switch";
        }

        if (name.Contains("Steam", StringComparison.OrdinalIgnoreCase))
        {
            return "Steam";
        }

        if (name.Contains("Xbox", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("XInput", StringComparison.OrdinalIgnoreCase))
        {
            return "Xbox";
        }

        if (pad.Family == PadFamily.PlayStation)
        {
            return "PlayStation";
        }

        if (pad.Family == PadFamily.Nintendo)
        {
            return "Switch";
        }

        if (pad.Family == PadFamily.Xbox)
        {
            return "Xbox";
        }

        return name.Length <= 20 ? name : name[..20] + "…";
    }

    private void UpdatePressedStrip(ControllerSnapshot snapshot)
    {
        var labels = snapshot.Inputs.Select(input => InputCatalog.Label(input, snapshot.Family)).ToList();
        var signature = string.Join("|", labels);
        if (string.Equals(signature, _lastPressedSignature, StringComparison.Ordinal))
        {
            return;
        }

        _lastPressedSignature = signature;
        PressedItems.ItemsSource = labels;
        PressedNoneText.Visibility = labels.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnHotkeyEvent(EngineEvent engineEvent)
    {
        if (engineEvent.Kind != EngineEventKind.Activated)
        {
            return;
        }

        var name = engineEvent.HotkeyName ?? "Hotkey";
        var time = DateTime.Now.ToString("HH:mm:ss");
        Dispatcher.BeginInvoke(() =>
        {
            LastFiredText.Text = $"Last fired: {name} at {time}";
        }, DispatcherPriority.Background);
    }

    private void RebuildBanners()
    {
        BannerPanel.Children.Clear();

        if (!string.IsNullOrWhiteSpace(_configError))
        {
            AddBanner(_configError, isError: true);
        }

        if (!_elevated)
        {
            AddBanner(
                "NashmiKeys is not running as administrator. Windows may block injected keys in elevated apps and games. Restart NashmiKeys as administrator.",
                isError: false);
        }

        if (!string.IsNullOrWhiteSpace(_lastSdlError))
        {
            var suffix = _lastSdlError.Contains("SDL3.dll", StringComparison.OrdinalIgnoreCase)
                ? " Editing hotkeys still works."
                : " Put the 64-bit SDL3.dll next to NashmiKeys.exe, then restart the app. Editing hotkeys still works.";
            AddBanner(_lastSdlError + suffix, isError: true);
        }

        BannerPanel.Visibility = BannerPanel.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AddBanner(string message, bool isError)
    {
        var border = new Border
        {
            Background = (MediaBrush)FindResource(isError ? "DangerSoftBrush" : "WarningSoftBrush"),
            BorderBrush = (MediaBrush)FindResource(isError ? "DangerBrush" : "WarningBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 9, 12, 9),
            Margin = new Thickness(0, 0, 0, 8)
        };

        var panel = new Grid();
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var mark = new TextBlock
        {
            Text = "!",
            FontWeight = FontWeights.Bold,
            Foreground = (MediaBrush)FindResource(isError ? "DangerBrush" : "WarningBrush"),
            Margin = new Thickness(0, 0, 9, 0),
            VerticalAlignment = VerticalAlignment.Top
        };
        var text = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (MediaBrush)FindResource("TextBrush")
        };
        Grid.SetColumn(text, 1);

        panel.Children.Add(mark);
        panel.Children.Add(text);
        border.Child = panel;
        BannerPanel.Children.Add(border);
    }

    private void PauseToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_pauseUiUpdating)
        {
            return;
        }

        SetPaused(PauseToggle.IsChecked == true);
    }

    private void SetPaused(bool paused)
    {
        _config.Settings.Paused = paused;
        _service.SetPaused(paused);
        UpdatePauseUi();

        var error = PersistConfig(updateEngine: false);
        if (error is not null)
        {
            ShowError("Hotkeys were paused, but the setting could not be saved", error);
        }
    }

    private void UpdatePauseUi()
    {
        _pauseUiUpdating = true;
        PauseToggle.IsChecked = _config.Settings.Paused;
        PauseToggle.Content = _config.Settings.Paused ? "Resume hotkeys" : "Pause hotkeys";
        PauseToggle.ToolTip = _config.Settings.Paused
            ? "Hotkeys are paused and all injected keys are released"
            : "Stop all hotkey output without closing the app";
        _pauseUiUpdating = false;

        if (_trayPauseItem is not null)
        {
            _trayPauseItem.Checked = _config.Settings.Paused;
            _trayPauseItem.Text = _config.Settings.Paused ? "Resume hotkeys" : "Pause hotkeys";
        }

        if (_tray is not null)
        {
            _tray.Text = _config.Settings.Paused ? "NashmiKeys (paused)" : "NashmiKeys";
        }
    }

    private void TogglePauseFromTray()
    {
        SetPaused(!_config.Settings.Paused);
    }

    private void ToggleStartWithWindowsFromTray()
    {
        var enable = !_config.Settings.StartWithWindows;
        ToggleStartWithWindows(enable);
    }

    private void ToggleStartWithWindows(bool enable)
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath))
        {
            ShowError("Could not register startup", "The executable path could not be determined.");
            return;
        }

        var result = enable
            ? _startup.SetEnabled(true, exePath, _config.Settings.StartMinimized)
            : _startup.DeleteTask();

        if (!result.Success)
        {
            ShowError(
                enable ? "Could not register Start with Windows" : "Could not disable Start with Windows",
                string.IsNullOrWhiteSpace(result.Output)
                    ? "schtasks.exe returned an error. The app must be elevated to change the scheduled task."
                    : result.Output);
            UpdateTrayChecks();
            return;
        }

        var previous = _config.Settings.StartWithWindows;
        _config.Settings.StartWithWindows = enable;
        var error = PersistConfig(updateEngine: false);
        if (error is not null)
        {
            _config.Settings.StartWithWindows = previous;
            try
            {
                if (previous)
                {
                    _startup.SetEnabled(true, exePath, _config.Settings.StartMinimized);
                }
                else
                {
                    _startup.DeleteTask();
                }
            }
            catch (Exception exception)
            {
                _logger.Warn("Could not roll back the scheduled task: " + exception.Message);
            }

            ShowError("Start with Windows changed, but the setting could not be saved", error);
        }

        UpdateTrayChecks();
    }

    private void UpdateTrayChecks()
    {
        if (_trayPauseItem is not null)
        {
            _trayPauseItem.Checked = _config.Settings.Paused;
        }

        if (_trayStartupItem is not null)
        {
            _trayStartupItem.Checked = _config.Settings.StartWithWindows;
        }
    }

    private void AddHotkey_Click(object sender, RoutedEventArgs e)
    {
        FilePopup.IsOpen = false;
        var hotkey = new Hotkey
        {
            Id = Guid.NewGuid().ToString("D"),
            Name = string.Empty,
            Enabled = true,
            Mode = HotkeyMode.Hold,
            Inputs = new List<string>(),
            Outputs = new List<string>()
        };

        OpenEditor(hotkey, isNew: true);
    }

    private void EditHotkey_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not HotkeyCardViewModel card)
        {
            return;
        }

        OpenEditor(card.Model.Clone(), isNew: false);
    }

    private void DuplicateHotkey_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not HotkeyCardViewModel card)
        {
            return;
        }

        var clone = card.Model.CloneWithNewId();
        clone.Name = string.IsNullOrWhiteSpace(card.Model.Name) ? "Hotkey copy" : card.Model.Name + " copy";
        var index = _config.Hotkeys.IndexOf(card.Model);
        _config.Hotkeys.Insert(index >= 0 ? index + 1 : _config.Hotkeys.Count, clone);

        var error = PersistConfig(updateEngine: true);
        if (error is not null)
        {
            _config.Hotkeys.Remove(clone);
            ShowError("Could not duplicate the hotkey", error);
        }
        else
        {
            BuildCards();
        }
    }

    private void DeleteHotkey_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not HotkeyCardViewModel card)
        {
            return;
        }

        var name = string.IsNullOrWhiteSpace(card.Model.Name) ? "this hotkey" : $"\"{card.Model.Name}\"";
        var confirm = new ConfirmDialog(
            "Delete hotkey",
            $"Delete {name}? This cannot be undone.",
            "Delete")
        {
            Owner = this
        };

        if (confirm.ShowDialog() != true)
        {
            return;
        }

        var index = _config.Hotkeys.IndexOf(card.Model);
        _config.Hotkeys.Remove(card.Model);
        var error = PersistConfig(updateEngine: true);
        if (error is not null)
        {
            if (index >= 0)
            {
                _config.Hotkeys.Insert(index, card.Model);
            }
            else
            {
                _config.Hotkeys.Add(card.Model);
            }

            ShowError("Could not delete the hotkey", error);
        }
        else
        {
            BuildCards();
        }
    }

    private void OpenEditor(Hotkey hotkey, bool isNew)
    {
        var others = _config.Hotkeys
            .Where(existing => !string.Equals(existing.Id, hotkey.Id, StringComparison.Ordinal))
            .ToList();

        var editor = new HotkeyEditorWindow(hotkey, isNew, _service, others, SaveFromEditor)
        {
            Owner = this
        };

        _service.SetSuppressed(true);
        try
        {
            editor.ShowDialog();
        }
        finally
        {
            _service.SetSuppressed(false);
        }
    }

    private string? SaveFromEditor(Hotkey edited, bool isNew)
    {
        var existing = _config.Hotkeys.FirstOrDefault(hotkey =>
            string.Equals(hotkey.Id, edited.Id, StringComparison.Ordinal));
        Hotkey? original = existing?.Clone();
        var index = existing is null ? -1 : _config.Hotkeys.IndexOf(existing);

        if (existing is null)
        {
            _config.Hotkeys.Add(edited);
        }
        else
        {
            _config.Hotkeys[index] = edited;
        }

        var error = PersistConfig(updateEngine: true);
        if (error is not null)
        {
            if (existing is null)
            {
                _config.Hotkeys.Remove(edited);
            }
            else if (original is not null)
            {
                _config.Hotkeys[index] = original;
            }

            return error;
        }

        BuildCards();
        return null;
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        FilePopup.IsOpen = false;

        var settings = new SettingsWindow(
            _config,
            _service,
            _startup,
            _logger,
            _elevated,
            ApplySettingsFromDialog)
        {
            Owner = this
        };

        _service.SetSuppressed(true);
        try
        {
            if (settings.ShowDialog() == true)
            {
                UpdatePauseUi();
                UpdateTrayChecks();
            }
        }
        finally
        {
            _service.SetSuppressed(false);
        }
    }

    private string? ApplySettingsFromDialog(AppSettings settings)
    {
        var previous = _config.Settings;
        _config.Settings = settings;
        _service.UpdateSettings(settings);
        var error = PersistConfig(updateEngine: false);
        if (error is not null)
        {
            _config.Settings = previous;
            _service.UpdateSettings(previous);
            return error;
        }

        UpdateTrayChecks();
        return null;
    }

    private void FileButton_Click(object sender, RoutedEventArgs e)
    {
        FilePopup.IsOpen = !FilePopup.IsOpen;
    }

    private void OpenConfigFolder_Click(object sender, RoutedEventArgs e)
    {
        FilePopup.IsOpen = false;
        OpenFolder(AppPaths.DataDirectory);
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        FilePopup.IsOpen = false;
        RequestExit();
    }

    public void RequestExit()
    {
        if (_exitRequested)
        {
            return;
        }

        _exitRequested = true;
        _allowClose = true;
        DisposeTray();
        ExitRequested?.Invoke();
    }

    public void PrepareForExit()
    {
        _allowClose = true;
        DisposeTray();
    }

    public void ShowFromTray()
    {
        if (!IsVisible)
        {
            Show();
        }

        ShowInTaskbar = true;
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
        RefreshFromController();
    }

    private void HideToTray()
    {
        if (_tray is not null && !_balloonShown)
        {
            _balloonShown = true;
            _tray.ShowBalloonTip(
                3000,
                "NashmiKeys",
                "NashmiKeys is still running in the tray. Use the tray menu to exit.",
                Forms.ToolTipIcon.Info);
        }

        Hide();
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && _config.Settings.MinimizeToTray && !_allowClose)
        {
            HideToTray();
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        if (_config.Settings.MinimizeToTray)
        {
            HideToTray();
        }
        else
        {
            WindowState = WindowState.Minimized;
        }
    }

    private string? PersistConfig(bool updateEngine)
    {
        try
        {
            _store.Save(_config);
            if (updateEngine)
            {
                _service.UpdateHotkeys(_config.Hotkeys);
            }

            UpdateEmptyState();
            return null;
        }
        catch (Exception exception)
        {
            _logger.Error("Could not save config.", exception);
            return exception.Message;
        }
    }

    private void OpenFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo
            {
                FileName = folder,
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            _logger.Error("Could not open folder " + folder, exception);
            ShowError("Could not open folder", exception.Message);
        }
    }

    private void ShowError(string title, string message)
    {
        System.Windows.MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void DisposeTray()
    {
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Icon = null;
            _tray.Dispose();
            _tray = null;
        }

        _trayImage?.Dispose();
        _trayImage = null;
    }

    private sealed class DarkMenuRenderer : Forms.ToolStripProfessionalRenderer
    {
        private static readonly Drawing.Color Background = Drawing.Color.FromArgb(0x12, 0x16, 0x1E);
        private static readonly Drawing.Color Hover = Drawing.Color.FromArgb(0x22, 0x29, 0x36);
        private static readonly Drawing.Color Text = Drawing.Color.FromArgb(0xE8, 0xEC, 0xF3);
        private static readonly Drawing.Color Border = Drawing.Color.FromArgb(0x35, 0x40, 0x52);

        protected override void OnRenderToolStripBackground(Forms.ToolStripRenderEventArgs e)
        {
            using var brush = new Drawing.SolidBrush(Background);
            e.Graphics.FillRectangle(brush, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(Forms.ToolStripRenderEventArgs e)
        {
            using var pen = new Drawing.Pen(Border);
            e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        }

        protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected)
            {
                return;
            }

            using var brush = new Drawing.SolidBrush(Hover);
            e.Graphics.FillRectangle(brush, new Drawing.Rectangle(2, 1, e.Item.Width - 4, e.Item.Height - 2));
        }

        protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Text : Drawing.Color.FromArgb(0x6B, 0x76, 0x86);
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(Forms.ToolStripSeparatorRenderEventArgs e)
        {
            using var pen = new Drawing.Pen(Border);
            var y = e.Item.Height / 2;
            e.Graphics.DrawLine(pen, 8, y, e.Item.Width - 8, y);
        }

        protected override void OnRenderItemCheck(Forms.ToolStripItemImageRenderEventArgs e)
        {
            var area = e.ImageRectangle;
            if (area.IsEmpty)
            {
                return;
            }

            using var pen = new Drawing.Pen(Drawing.Color.FromArgb(0x7A, 0xA2, 0xFF), 2f);
            e.Graphics.DrawLines(pen, new[]
            {
                new Drawing.Point(area.Left + 2, area.Top + area.Height / 2),
                new Drawing.Point(area.Left + area.Width / 2, area.Bottom - 3),
                new Drawing.Point(area.Right - 2, area.Top + 3)
            });
        }
    }
}
