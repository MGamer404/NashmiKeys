using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using NashmiKeys.Core.Input;
using NashmiKeys.Core.Logging;

namespace NashmiKeys.Core.Configuration;

public sealed record ConfigLoadResult(
    AppConfig Config,
    string? Error,
    string? BackupPath,
    bool WasCreated);

public sealed class ConfigStore
{
    private readonly string _directory;
    private readonly string _filePath;
    private readonly ILogger _logger;
    private readonly JsonSerializerOptions _jsonOptions;

    public ConfigStore(string? directory = null, ILogger? logger = null)
    {
        _directory = directory ?? AppPaths.DataDirectory;
        _filePath = Path.Combine(_directory, "config.json");
        _logger = logger ?? NullLogger.Instance;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
    }

    public string FilePath => _filePath;

    public ConfigLoadResult Load()
    {
        try
        {
            Directory.CreateDirectory(_directory);
        }
        catch (Exception exception)
        {
            _logger.Error("Could not create the config directory.", exception);
        }

        if (!File.Exists(_filePath))
        {
            var defaults = AppConfig.CreateDefault();
            try
            {
                Save(defaults);
            }
            catch (Exception exception)
            {
                _logger.Error("Could not write the initial config file.", exception);
            }

            return new ConfigLoadResult(defaults, null, null, true);
        }

        try
        {
            var json = File.ReadAllText(_filePath, Encoding.UTF8);
            var config = JsonSerializer.Deserialize<AppConfig>(json, _jsonOptions)
                         ?? throw new InvalidOperationException("The config file contains no JSON object.");
            Normalize(config);
            return new ConfigLoadResult(config, null, null, false);
        }
        catch (Exception exception)
        {
            _logger.Error($"Could not load config '{_filePath}'. Falling back to defaults.", exception);
            string? backupPath = null;
            try
            {
                backupPath = Path.Combine(
                    _directory,
                    $"config.invalid-{DateTime.Now:yyyyMMdd-HHmmss}.json");
                File.Copy(_filePath, backupPath, overwrite: true);
            }
            catch (Exception backupException)
            {
                _logger.Warn("Could not back up the invalid config: " + backupException.Message);
            }

            var defaults = AppConfig.CreateDefault();
            try
            {
                Save(defaults);
            }
            catch (Exception saveException)
            {
                _logger.Error("Could not save default config after a load failure.", saveException);
            }

            var backupNote = backupPath is null ? string.Empty : $" The invalid file was backed up to {backupPath}.";
            return new ConfigLoadResult(
                defaults,
                "Could not read config.json: " + exception.Message + backupNote,
                backupPath,
                false);
        }
    }

    public void Save(AppConfig config)
    {
        Normalize(config);
        Directory.CreateDirectory(_directory);

        var json = JsonSerializer.Serialize(config, _jsonOptions);
        var temporaryPath = _filePath + ".tmp";

        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.Create,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(_filePath))
            {
                try
                {
                    File.Replace(temporaryPath, _filePath, destinationBackupFileName: null, ignoreMetadataErrors: true);
                }
                catch (Exception exception) when (exception is IOException or PlatformNotSupportedException or UnauthorizedAccessException)
                {
                    File.Move(temporaryPath, _filePath, overwrite: true);
                }
            }
            else
            {
                File.Move(temporaryPath, _filePath);
            }
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
                // Best effort cleanup.
            }
        }
    }

    public static void Normalize(AppConfig config)
    {
        config.Version = config.Version <= 0 ? 1 : config.Version;
        config.Settings ??= new AppSettings();
        config.Hotkeys ??= new List<Hotkey>();

        config.Settings.ComboHoldMs = Math.Clamp(config.Settings.ComboHoldMs, 0, 500);
        config.Settings.L2Threshold = Math.Clamp(config.Settings.L2Threshold, 0.05, 0.99);
        config.Settings.R2Threshold = Math.Clamp(config.Settings.R2Threshold, 0.05, 0.99);

        var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hotkey in config.Hotkeys)
        {
            if (hotkey is null)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(hotkey.Id) || !usedIds.Add(hotkey.Id))
            {
                hotkey.Id = Guid.NewGuid().ToString("D");
                usedIds.Add(hotkey.Id);
            }

            hotkey.Name ??= "Hotkey";
            hotkey.Inputs = InputCatalog.NormalizeSet(hotkey.Inputs ?? new List<string>()).ToList();
            hotkey.Outputs = KeyCatalog.NormalizeOutputs(hotkey.Outputs ?? new List<string>()).ToList();
            if (!Enum.IsDefined(typeof(HotkeyMode), hotkey.Mode))
            {
                hotkey.Mode = HotkeyMode.Hold;
            }
        }

        config.Hotkeys.RemoveAll(hotkey => hotkey is null);
    }
}
