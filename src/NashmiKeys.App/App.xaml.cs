using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using NashmiKeys.App.Services;
using NashmiKeys.App.Views;
using NashmiKeys.Core;
using NashmiKeys.Core.Configuration;
using NashmiKeys.Core.Diagnostics;
using NashmiKeys.Core.Engine;
using NashmiKeys.Core.Injection;
using NashmiKeys.Core.Logging;
using NashmiKeys.Core.Sdl;
using NashmiKeys.Core.Startup;
using NashmiKeys.Core.Windows;

namespace NashmiKeys.App;

public partial class App : System.Windows.Application
{
    private const string MutexName = @"Local\NashmiKeys.SingleInstance.v1";
    private const string ActivateEventName = @"Local\NashmiKeys.Activate.v1";

    private ILogger _logger = NullLogger.Instance;
    private FileLogger? _fileLogger;
    private Mutex? _mutex;
    private bool _ownsMutex;
    private EventWaitHandle? _activationEvent;
    private Thread? _activationThread;
    private HotkeyService? _service;
    private MainWindow? _mainWindow;
    private bool _exiting;
    private bool _serviceDisposed;
    private bool _handlingUnhandledException;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Installer hooks. These must run before the single-instance mutex below
        // so setup and uninstall can update the scheduled task while the app runs.
        var autostartExitCode = TryRunAutostartCommand(e.Args);
        if (autostartExitCode is not null)
        {
            Environment.ExitCode = autostartExitCode.Value;
            Shutdown(autostartExitCode.Value);
            return;
        }

        var selfTest = e.Args.Any(argument => string.Equals(argument, "--selftest", StringComparison.OrdinalIgnoreCase));
        if (selfTest)
        {
            RunSelfTestAndExit();
            return;
        }

        try
        {
            InitializeLogger();
            _logger.Info($"NashmiKeys starting. Version {GetType().Assembly.GetName().Version}; elevated={Elevation.IsElevated()}.");
            _logger.Info($"Process path: {Environment.ProcessPath}");

            if (!AcquireSingleInstance())
            {
                _logger.Info("Another instance is already running; asked it to show its window.");
                Shutdown(0);
                return;
            }

            MigrateLegacyConfig();

            var startup = new StartupManager(_logger);
            RemoveLegacyAutostartTask(startup);

            var store = new ConfigStore(AppPaths.DataDirectory, _logger);
            var loadResult = store.Load();
            if (loadResult.Error is not null)
            {
                _logger.Warn("Config load error: " + loadResult.Error);
            }

            _logger.Info($"Config loaded from {store.FilePath}. Hotkeys: {loadResult.Config.Hotkeys.Count}.");

            var injector = new SendInputInjector(_logger);
            var engine = new HotkeyEngine(
                loadResult.Config.Hotkeys,
                injector,
                loadResult.Config.Settings.ComboHoldMs,
                _logger);
            var poller = new GamepadPoller(
                _logger,
                loadResult.Config.Settings.L2Threshold,
                loadResult.Config.Settings.R2Threshold);
            _service = new HotkeyService(engine, poller, _logger, loadResult.Config.Settings.Paused);

            var elevated = Elevation.IsElevated();
            _mainWindow = new MainWindow(
                loadResult.Config,
                store,
                _service,
                startup,
                _logger,
                loadResult.Error,
                elevated);
            _mainWindow.ExitRequested += ExitApplication;
            MainWindow = _mainWindow;

            _service.Start();
            StartActivationListener();

            var startHidden = loadResult.Config.Settings.StartMinimized ||
                              e.Args.Any(argument => string.Equals(argument, "--minimized", StringComparison.OrdinalIgnoreCase));
            if (startHidden)
            {
                _logger.Info("Starting hidden in the tray.");
            }
            else
            {
                _mainWindow.Show();
            }

            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
            SessionEnding += OnSessionEnding;
        }
        catch (Exception exception)
        {
            _logger.Error("NashmiKeys failed to start.", exception);
            System.Windows.MessageBox.Show(
                "NashmiKeys could not start.\n\n" + exception.Message +
                "\n\nSee the log at " + AppPaths.LogFilePath + " for details.",
                "NashmiKeys",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void InitializeLogger()
    {
        try
        {
            _fileLogger = new FileLogger(AppPaths.LogFilePath);
            _logger = _fileLogger;
        }
        catch
        {
            _logger = NullLogger.Instance;
        }
    }

    /// <summary>
    /// Copies the pre-rename config into the new data folder on first run.
    /// The old folder is deliberately left untouched.
    /// </summary>
    private void MigrateLegacyConfig()
    {
        try
        {
            if (File.Exists(AppPaths.ConfigFilePath) || !File.Exists(AppPaths.LegacyConfigFilePath))
            {
                return;
            }

            Directory.CreateDirectory(AppPaths.DataDirectory);
            File.Copy(AppPaths.LegacyConfigFilePath, AppPaths.ConfigFilePath, overwrite: false);
            _logger.Info(
                $"Migrated config from {AppPaths.LegacyConfigFilePath} to {AppPaths.ConfigFilePath}. " +
                "The old folder was left untouched.");
        }
        catch (Exception exception)
        {
            _logger.Warn("Could not migrate the config from " + AppPaths.LegacyConfigFilePath + ": " + exception.Message);
        }
    }

    private void RemoveLegacyAutostartTask(StartupManager startup)
    {
        try
        {
            var result = startup.RemoveLegacyTask();
            if (!result.Success)
            {
                _logger.Warn("Could not remove the legacy PadHotkeys scheduled task: " + result.Output);
            }
        }
        catch (Exception exception)
        {
            _logger.Warn("Could not remove the legacy PadHotkeys scheduled task: " + exception.Message);
        }
    }

    private int? TryRunAutostartCommand(string[] args)
    {
        var register = args.Any(argument => string.Equals(argument, "--register-autostart", StringComparison.OrdinalIgnoreCase));
        var unregister = args.Any(argument => string.Equals(argument, "--unregister-autostart", StringComparison.OrdinalIgnoreCase));
        if (!register && !unregister)
        {
            return null;
        }

        InitializeLogger();
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath))
            {
                exePath = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
            }

            var startMinimized = args.Any(argument => string.Equals(argument, "--minimized", StringComparison.OrdinalIgnoreCase));
            var startup = new StartupManager(_logger);
            var result = unregister
                ? startup.DeleteAllTasks()
                : startup.SetEnabled(true, exePath, startMinimized);

            _logger.Info(
                $"Autostart command {(unregister ? "--unregister-autostart" : "--register-autostart")} for {exePath} " +
                $"(minimized={startMinimized}) finished with exit code {result.ExitCode}. Output: {result.Output}");

            return result.Success ? 0 : 1;
        }
        catch (Exception exception)
        {
            _logger.Error("Autostart command failed.", exception);
            return 1;
        }
        finally
        {
            _fileLogger?.Dispose();
            _fileLogger = null;
        }
    }

    private void RunSelfTestAndExit()
    {
        InitializeLogger();
        _logger.Info("=== NashmiKeys --selftest ===");
        var writer = new StringWriter();
        var exitCode = SelfTestSuite.Run(writer, _logger);
        _logger.Info(writer.ToString());
        _logger.Info("=== NashmiKeys --selftest exit code " + exitCode + " ===");
        _fileLogger?.Dispose();
        Environment.ExitCode = exitCode;
        Shutdown(exitCode);
    }

    private bool AcquireSingleInstance()
    {
        var createdNew = true;
        try
        {
            _mutex = new Mutex(initiallyOwned: true, MutexName, out createdNew);
        }
        catch (Exception exception)
        {
            _logger.Warn("Single-instance mutex could not be created: " + exception.Message);
            return true;
        }

        if (!createdNew)
        {
            SignalExistingInstance();
            return false;
        }

        _ownsMutex = true;
        try
        {
            _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        }
        catch (Exception exception)
        {
            _logger.Warn("Activation event could not be created: " + exception.Message);
        }

        return true;
    }

    private static void SignalExistingInstance()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                using var activation = EventWaitHandle.OpenExisting(ActivateEventName);
                activation.Set();
                return;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                Thread.Sleep(50);
            }
            catch
            {
                return;
            }
        }
    }

    private void StartActivationListener()
    {
        if (_activationEvent is null)
        {
            return;
        }

        _activationThread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    if (!_activationEvent.WaitOne())
                    {
                        return;
                    }
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (Exception)
                {
                    return;
                }

                try
                {
                    Dispatcher.BeginInvoke(() => _mainWindow?.ShowFromTray());
                }
                catch
                {
                    return;
                }
            }
        })
        {
            IsBackground = true,
            Name = "NashmiKeys.Activation"
        };
        _activationThread.Start();
    }

    private void ExitApplication()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        _logger.Info("Exit requested.");
        DisposeService();
        _mainWindow?.PrepareForExit();
        Shutdown(0);
    }

    private void DisposeService()
    {
        if (_serviceDisposed)
        {
            return;
        }

        _serviceDisposed = true;
        try
        {
            _service?.Dispose();
        }
        catch (Exception exception)
        {
            _logger.Error("Error while disposing the hotkey service.", exception);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger.Error("Unhandled UI exception.", e.Exception);
        try
        {
            _service?.ReleaseAll("unhandled error");
        }
        catch
        {
            // Best effort.
        }

        if (!_handlingUnhandledException)
        {
            _handlingUnhandledException = true;
            System.Windows.MessageBox.Show(
                "NashmiKeys hit an unexpected error. All injected keys were released.\n\n" + e.Exception.Message,
                "NashmiKeys",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            _handlingUnhandledException = false;
        }

        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            _logger.Error("Unhandled process exception.", exception);
        }

        try
        {
            _service?.ReleaseAll("unhandled error");
        }
        catch
        {
            // Best effort.
        }
    }

    private void OnSessionEnding(object sender, SessionEndingCancelEventArgs e)
    {
        _logger.Info("Windows session ending; releasing keys.");
        DisposeService();
        _mainWindow?.PrepareForExit();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DisposeService();
        _mainWindow?.PrepareForExit();

        try
        {
            if (_activationEvent is not null)
            {
                _activationEvent.Set();
            }
        }
        catch
        {
            // Ignore.
        }

        try
        {
            _activationEvent?.Dispose();
        }
        catch
        {
            // Ignore.
        }

        try
        {
            if (_mutex is not null)
            {
                if (_ownsMutex)
                {
                    _mutex.ReleaseMutex();
                }

                _mutex.Dispose();
            }
        }
        catch
        {
            // Ignore.
        }

        _logger.Info("NashmiKeys exited with code " + e.ApplicationExitCode + ".");
        _fileLogger?.Dispose();
        base.OnExit(e);
    }
}
