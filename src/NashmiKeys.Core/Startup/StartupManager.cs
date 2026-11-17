using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using NashmiKeys.Core.Logging;

namespace NashmiKeys.Core.Startup;

public sealed record StartupTaskResult(bool Success, int ExitCode, string Output);

/// <summary>
/// Registers an elevated Task Scheduler task so Windows starts the app at
/// logon without a UAC prompt. Uses schtasks.exe with a generated XML file.
/// </summary>
public sealed class StartupManager
{
    public const string TaskName = "NashmiKeys";
    public const string LegacyTaskName = "PadHotkeys";
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    private readonly ILogger _logger;

    public StartupManager(ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    public static string BuildTaskXml(string exePath, string userId, bool startMinimized)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            throw new ArgumentException("Executable path is required.", nameof(exePath));
        }

        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new ArgumentException("User id is required.", nameof(userId));
        }

        var settings = new XElement(Ns + "Settings",
            new XElement(Ns + "MultipleInstancesPolicy", "IgnoreNew"),
            new XElement(Ns + "DisallowStartIfOnBatteries", "false"),
            new XElement(Ns + "StopIfGoingOnBatteries", "false"),
            new XElement(Ns + "AllowHardTerminate", "true"),
            new XElement(Ns + "StartWhenAvailable", "true"),
            new XElement(Ns + "RunOnlyIfNetworkAvailable", "false"),
            new XElement(Ns + "IdleSettings",
                new XElement(Ns + "StopOnIdleEnd", "false"),
                new XElement(Ns + "RestartOnIdle", "false")),
            new XElement(Ns + "AllowStartOnDemand", "true"),
            new XElement(Ns + "Enabled", "true"),
            new XElement(Ns + "Hidden", "false"),
            new XElement(Ns + "RunOnlyIfIdle", "false"),
            new XElement(Ns + "WakeToRun", "false"),
            new XElement(Ns + "ExecutionTimeLimit", "PT0S"),
            new XElement(Ns + "Priority", "7"));

        var action = new XElement(Ns + "Exec",
            new XElement(Ns + "Command", "\"" + exePath + "\""));
        if (startMinimized)
        {
            action.Add(new XElement(Ns + "Arguments", "--minimized"));
        }

        var document = new XDocument(
            new XDeclaration("1.0", "utf-16", null),
            new XElement(Ns + "Task",
                new XAttribute("version", "1.2"),
                new XElement(Ns + "RegistrationInfo",
                    new XElement(Ns + "Description", "NashmiKeys controller chord hotkeys"),
                    new XElement(Ns + "Author", userId)),
                new XElement(Ns + "Triggers",
                    new XElement(Ns + "LogonTrigger",
                        new XElement(Ns + "Enabled", "true"),
                        new XElement(Ns + "UserId", userId))),
                new XElement(Ns + "Principals",
                    new XElement(Ns + "Principal",
                        new XAttribute("id", "Author"),
                        new XElement(Ns + "UserId", userId),
                        new XElement(Ns + "LogonType", "InteractiveToken"),
                        new XElement(Ns + "RunLevel", "HighestAvailable"))),
                settings,
                new XElement(Ns + "Actions",
                    new XAttribute("Context", "Author"),
                    action)));

        var builder = new StringBuilder();
        var writerSettings = new XmlWriterSettings
        {
            Indent = true,
            Encoding = Encoding.Unicode,
            OmitXmlDeclaration = false
        };

        using (var stringWriter = new StringWriter(builder))
        using (var xmlWriter = XmlWriter.Create(stringWriter, writerSettings))
        {
            document.Save(xmlWriter);
        }

        return builder.ToString();
    }

    public StartupTaskResult SetEnabled(bool enabled, string exePath, bool startMinimized)
    {
        if (!enabled)
        {
            return DeleteTask();
        }

        // A rename leaves the old task behind; never run both at logon.
        RemoveLegacyTask();

        string temporaryPath = Path.Combine(
            Path.GetTempPath(),
            "NashmiKeys-task-" + Guid.NewGuid().ToString("N") + ".xml");

        try
        {
            var userId = WindowsIdentity.GetCurrent().Name;
            var xml = BuildTaskXml(exePath, userId, startMinimized);
            File.WriteAllText(temporaryPath, xml, new UnicodeEncoding(bigEndian: false, byteOrderMark: true));

            var result = RunSchtasks("/Create", "/TN", TaskName, "/XML", temporaryPath, "/F");
            if (result.Success)
            {
                _logger.Info($"Scheduled task '{TaskName}' created for {userId}.");
            }
            else
            {
                _logger.Error($"Could not create scheduled task '{TaskName}': {result.Output}");
            }

            return result;
        }
        catch (Exception exception)
        {
            _logger.Error("Could not create the scheduled task.", exception);
            return new StartupTaskResult(false, -1, exception.Message);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch
            {
                // Temp file cleanup is best effort.
            }
        }
    }

    public StartupTaskResult DeleteTask()
    {
        var result = RunSchtasks("/Delete", "/TN", TaskName, "/F");
        if (result.Success)
        {
            _logger.Info($"Scheduled task '{TaskName}' deleted.");
            return result;
        }

        if (result.Output.Contains("cannot find", StringComparison.OrdinalIgnoreCase))
        {
            _logger.Info($"Scheduled task '{TaskName}' was not registered; nothing to delete.");
            return new StartupTaskResult(true, result.ExitCode, result.Output);
        }

        _logger.Error($"Could not delete scheduled task '{TaskName}': {result.Output}");
        return result;
    }

    public bool IsRegistered(out string detail)
    {
        var result = RunSchtasks("/Query", "/TN", TaskName);
        detail = result.Output;
        return result.Success;
    }

    /// <summary>
    /// Removes the scheduled task left by the pre-rename builds. Safe to call
    /// when no such task exists.
    /// </summary>
    public StartupTaskResult RemoveLegacyTask()
    {
        var query = RunSchtasks("/Query", "/TN", LegacyTaskName);
        if (!query.Success)
        {
            return new StartupTaskResult(true, 0, query.Output);
        }

        var result = RunSchtasks("/Delete", "/TN", LegacyTaskName, "/F");
        if (result.Success)
        {
            _logger.Info($"Legacy scheduled task '{LegacyTaskName}' deleted.");
            return result;
        }

        _logger.Warn($"Could not delete legacy scheduled task '{LegacyTaskName}': {result.Output}");
        return result;
    }

    /// <summary>Removes both the current and the pre-rename tasks.</summary>
    public StartupTaskResult DeleteAllTasks()
    {
        var current = DeleteTask();
        var legacy = RemoveLegacyTask();
        var success = current.Success && legacy.Success;
        var exitCode = success ? 0 : (current.Success ? legacy.ExitCode : current.ExitCode);
        var output = (current.Output + Environment.NewLine + legacy.Output).Trim();
        return new StartupTaskResult(success, exitCode, output);
    }

    private StartupTaskResult RunSchtasks(params string[] arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new StartupTaskResult(false, -1, "Could not start schtasks.exe.");
            }

            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(20000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Ignore.
                }

                return new StartupTaskResult(false, -1, "schtasks.exe timed out.");
            }

            var output = (standardOutput.GetAwaiter().GetResult() + "\n" +
                          standardError.GetAwaiter().GetResult()).Trim();
            return new StartupTaskResult(process.ExitCode == 0, process.ExitCode, output);
        }
        catch (Exception exception)
        {
            return new StartupTaskResult(false, -1, exception.Message);
        }
    }
}
