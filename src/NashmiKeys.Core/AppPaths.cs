namespace NashmiKeys.Core;

public static class AppPaths
{
    public const string AppFolderName = "NashmiKeys";

    /// <summary>Folder used by the pre-rename builds; only read for migration.</summary>
    public const string LegacyAppFolderName = "PadHotkeys";

    public static string DataDirectory
    {
        get
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrWhiteSpace(appData))
            {
                appData = AppContext.BaseDirectory;
            }

            return Path.Combine(appData, AppFolderName);
        }
    }

    public static string LegacyDataDirectory
    {
        get
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrWhiteSpace(appData))
            {
                appData = AppContext.BaseDirectory;
            }

            return Path.Combine(appData, LegacyAppFolderName);
        }
    }

    public static string ConfigFilePath => Path.Combine(DataDirectory, "config.json");

    public static string LegacyConfigFilePath => Path.Combine(LegacyDataDirectory, "config.json");

    public static string LogDirectory => Path.Combine(DataDirectory, "logs");

    public static string LogFilePath => Path.Combine(LogDirectory, "app.log");
}
