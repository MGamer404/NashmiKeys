using System.Text;

namespace NashmiKeys.Core.Logging;

/// <summary>
/// Small file logger with size based rotation. Keeps app.log plus two backups
/// (three files total), each at most roughly maxBytes.
/// </summary>
public sealed class FileLogger : ILogger, IDisposable
{
    private readonly object _gate = new();
    private readonly string _filePath;
    private readonly long _maxBytes;
    private readonly int _maxFiles;
    private bool _disposed;

    public FileLogger(string filePath, long maxBytes = 1024 * 1024, int maxFiles = 3)
    {
        _filePath = filePath;
        _maxBytes = Math.Max(16 * 1024, maxBytes);
        _maxFiles = Math.Clamp(maxFiles, 2, 10);

        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    public void Info(string message) => Write("INFO", message, null);

    public void Warn(string message) => Write("WARN", message, null);

    public void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private void Write(string level, string message, Exception? exception)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            var builder = new StringBuilder(160);
            builder.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            builder.Append(" [").Append(level).Append("] ").Append(message);
            if (exception is not null)
            {
                builder.AppendLine();
                builder.Append(exception);
            }

            builder.AppendLine();
            var text = builder.ToString();

            lock (_gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
                RotateIfNeeded(Encoding.UTF8.GetByteCount(text));
                File.AppendAllText(_filePath, text, new UTF8Encoding(false));
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }

    private void RotateIfNeeded(int incomingBytes)
    {
        if (!File.Exists(_filePath))
        {
            return;
        }

        try
        {
            var length = new FileInfo(_filePath).Length;
            if (length + incomingBytes <= _maxBytes)
            {
                return;
            }

            for (var index = _maxFiles - 2; index >= 1; index--)
            {
                var source = GetBackupPath(index);
                var destination = GetBackupPath(index + 1);
                if (File.Exists(destination))
                {
                    File.Delete(destination);
                }

                if (File.Exists(source))
                {
                    File.Move(source, destination);
                }
            }

            var firstBackup = GetBackupPath(1);
            if (File.Exists(firstBackup))
            {
                File.Delete(firstBackup);
            }

            File.Move(_filePath, firstBackup);
        }
        catch
        {
            // If rotation fails we keep appending to the current file.
        }
    }

    private string GetBackupPath(int index)
    {
        var directory = Path.GetDirectoryName(_filePath)!;
        var name = Path.GetFileNameWithoutExtension(_filePath);
        var extension = Path.GetExtension(_filePath);
        return Path.Combine(directory, $"{name}.{index}{extension}");
    }

    public void Dispose()
    {
        _disposed = true;
    }
}
