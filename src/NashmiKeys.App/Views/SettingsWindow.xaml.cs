using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using NashmiKeys.App.Helpers;
using NashmiKeys.App.Services;
using NashmiKeys.Core;
using NashmiKeys.Core.Configuration;
using NashmiKeys.Core.Logging;
using NashmiKeys.Core.Startup;

namespace NashmiKeys.App.Views;

public partial class SettingsWindow : Window
{
    private readonly AppConfig _config;
    private readonly HotkeyService _service;
    private readonly StartupManager _startup;
    private readonly ILogger _logger;
    private readonly bool _elevated;
    private readonly Func<AppSettings, string?> _apply;
    private bool _taskRegistered;

    public SettingsWindow(
        AppConfig config,
        HotkeyService service,
        StartupManager startup,
        ILogger logger,
        bool elevated,
        Func<AppSettings, string?> apply)
    {
        InitializeComponent();
        WindowTheme.Apply(this);

        _config = config;
        _service = service;
        _startup = startup;
        _logger = logger;
        _elevated = elevated;
        _apply = apply;

        MaxHeight = Math.Max(520, SystemParameters.WorkArea.Height - 40);
        Height = Math.Min(740, MaxHeight);
        MaxWidth = Math.Max(600, SystemParameters.WorkArea.Width - 40);
        Width = Math.Min(680, MaxWidth);

        StartupToggle.IsChecked = config.Settings.StartWithWindows;
        StartMinimizedToggle.IsChecked = config.Settings.StartMinimized;
        MinimizeToTrayToggle.IsChecked = config.Settings.MinimizeToTray;
        ComboHoldSlider.Value = config.Settings.ComboHoldMs;
        L2Slider.Value = config.Settings.L2Threshold;
        R2Slider.Value = config.Settings.R2Threshold;

        UpdateValueLabels();
        StartupStatusText.Text = "Checking scheduled task…";
        FoldersText.Text = $"Config: {AppPaths.ConfigFilePath}\nLogs: {AppPaths.LogFilePath}";
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshDiagnostics(refreshTask: true);
    }

    private void ComboHoldSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateValueLabels();
    }

    private void L2Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateValueLabels();
    }

    private void R2Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateValueLabels();
    }

    private void UpdateValueLabels()
    {
        if (ComboHoldValueText is null || L2ValueText is null || R2ValueText is null)
        {
            return;
        }

        ComboHoldValueText.Text = $"{(int)Math.Round(ComboHoldSlider.Value)} ms";
        L2ValueText.Text = L2Slider.Value.ToString("0.00");
        R2ValueText.Text = R2Slider.Value.ToString("0.00");
    }

    private void RefreshDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        RefreshDiagnostics(refreshTask: true);
    }

    private void RefreshDiagnostics(bool refreshTask)
    {
        if (refreshTask)
        {
            _taskRegistered = _startup.IsRegistered(out var taskDetail);
            if (!_taskRegistered)
            {
                _logger.Info("Scheduled task query: not registered. " + taskDetail);
            }
        }

        StartupStatusText.Text = _taskRegistered
            ? "Scheduled task: registered."
            : _config.Settings.StartWithWindows
                ? "Scheduled task: NOT registered. Toggle Start with Windows off and on to repair it."
                : "Scheduled task: not registered.";

        var builder = new StringBuilder();
        builder.AppendLine(_elevated
            ? "Elevation: running as administrator."
            : "Elevation: NOT elevated — injected keys may be blocked in elevated apps.");
        builder.AppendLine("SDL: " + _service.SdlVersion);

        var snapshot = _service.LatestSnapshot;
        if (snapshot is null)
        {
            builder.AppendLine("Controller: waiting for the input thread.");
        }
        else if (snapshot.Pads.Count == 0)
        {
            builder.AppendLine(snapshot.DetectedCount > 0
                ? $"Controller: {snapshot.DetectedCount} detected but could not be opened. See the log."
                : "Controller: none connected.");
        }
        else
        {
            builder.AppendLine("Controller:");
            foreach (var pad in snapshot.Pads)
            {
                var battery = pad.BatteryPercent is null ? "battery n/a" : $"{pad.BatteryPercent}% ({pad.PowerState})";
                builder.AppendLine($"  • {pad.Name} — {pad.SdlTypeName} — {battery}");
            }
        }

        if (!string.IsNullOrWhiteSpace(_service.LastSdlError))
        {
            builder.AppendLine("Input error: " + _service.LastSdlError);
        }

        builder.Append("Config: ").AppendLine(AppPaths.ConfigFilePath);
        builder.Append("Logs: ").AppendLine(AppPaths.LogFilePath);

        DiagnosticsText.Text = builder.ToString().TrimEnd();
    }

    private void OpenConfigFolder_Click(object sender, RoutedEventArgs e)
    {
        OpenFolder(AppPaths.DataDirectory);
    }

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        OpenFolder(AppPaths.LogDirectory);
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
            ValidationText.Text = "Could not open the folder: " + exception.Message;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ValidationText.Text = string.Empty;

        var newSettings = new AppSettings
        {
            StartWithWindows = StartupToggle.IsChecked == true,
            StartMinimized = StartMinimizedToggle.IsChecked == true,
            MinimizeToTray = MinimizeToTrayToggle.IsChecked == true,
            ComboHoldMs = Math.Clamp((int)Math.Round(ComboHoldSlider.Value), 0, 500),
            L2Threshold = Math.Round(L2Slider.Value, 2),
            R2Threshold = Math.Round(R2Slider.Value, 2),
            Paused = _config.Settings.Paused
        };

        var old = _config.Settings;
        var exePath = Environment.ProcessPath;
        var taskNeedsUpdate = newSettings.StartWithWindows
            ? !old.StartWithWindows || newSettings.StartMinimized != old.StartMinimized || !_taskRegistered
            : _taskRegistered || old.StartWithWindows;

        if (taskNeedsUpdate)
        {
            if (string.IsNullOrWhiteSpace(exePath))
            {
                ValidationText.Text = "Could not determine the executable path.";
                return;
            }

            var result = newSettings.StartWithWindows
                ? _startup.SetEnabled(true, exePath, newSettings.StartMinimized)
                : _startup.DeleteTask();

            if (!result.Success)
            {
                var message = string.IsNullOrWhiteSpace(result.Output)
                    ? "Task Scheduler returned an error. NashmiKeys must run elevated to change the startup task."
                    : result.Output;
                ValidationText.Text = "Could not update the startup task.";
                System.Windows.MessageBox.Show(
                    this,
                    message,
                    "Could not update Start with Windows",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            _taskRegistered = newSettings.StartWithWindows;
        }

        var error = _apply(newSettings);
        if (error is not null)
        {
            TryRollbackTask(old, exePath);
            ValidationText.Text = "Could not save settings: " + error;
            return;
        }

        DialogResult = true;
    }

    private void TryRollbackTask(AppSettings old, string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return;
        }

        try
        {
            if (old.StartWithWindows)
            {
                _startup.SetEnabled(true, exePath, old.StartMinimized);
            }
            else
            {
                _startup.DeleteTask();
            }
        }
        catch (Exception exception)
        {
            _logger.Warn("Could not roll back the startup task: " + exception.Message);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
