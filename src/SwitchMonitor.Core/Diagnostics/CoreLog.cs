using System.Globalization;
using System.Text;

namespace SwitchMonitor.Core.Diagnostics;

public enum LogLevel { Debug, Info, Warning, Error }

public interface ICoreLog
{
    void Write(LogLevel level, string message, Exception? error = null);
}

public sealed class NullLog : ICoreLog
{
    public static NullLog Instance { get; } = new();
    public void Write(LogLevel level, string message, Exception? error = null) { }
}

/// <summary>Keeps entries in memory; used by tests and the diagnostics window.</summary>
public sealed class MemoryLog : ICoreLog
{
    private readonly object gate = new();
    private readonly List<string> entries = [];

    public IReadOnlyList<string> Entries { get { lock (gate) return entries.ToArray(); } }

    public void Write(LogLevel level, string message, Exception? error = null)
    {
        lock (gate) entries.Add(FileLog.Format(DateTimeOffset.Now, level, message, error));
    }
}

/// <summary>
/// Plain-text rotating log: switchmonitor.log, switchmonitor.1.log ... up to <see cref="KeepFiles"/>.
/// Never throws; a failing log must not break monitor control.
/// </summary>
public sealed class FileLog : ICoreLog
{
    private readonly object gate = new();
    private readonly string directory;
    private readonly string baseName;

    public FileLog(string directory, string baseName = "switchmonitor", long maxBytes = 512 * 1024, int keepFiles = 3)
    {
        this.directory = directory;
        this.baseName = baseName;
        MaxBytes = maxBytes;
        KeepFiles = Math.Max(1, keepFiles);
    }

    public long MaxBytes { get; }
    public int KeepFiles { get; }
    public LogLevel MinimumLevel { get; set; } = LogLevel.Info;
    public string CurrentPath => Path.Combine(directory, baseName + ".log");

    public void Write(LogLevel level, string message, Exception? error = null)
    {
        if (level < MinimumLevel) return;
        string line = Format(DateTimeOffset.Now, level, message, error) + Environment.NewLine;
        lock (gate)
        {
            try
            {
                Directory.CreateDirectory(directory);
                RotateIfNeeded(Encoding.UTF8.GetByteCount(line));
                File.AppendAllText(CurrentPath, line, Encoding.UTF8);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private void RotateIfNeeded(int incoming)
    {
        var current = new FileInfo(CurrentPath);
        if (!current.Exists || current.Length + incoming <= MaxBytes) return;
        string Archive(int n) => Path.Combine(directory, $"{baseName}.{n}.log");
        File.Delete(Archive(KeepFiles - 1 < 1 ? 1 : KeepFiles - 1));
        for (int n = KeepFiles - 2; n >= 1; n--)
            if (File.Exists(Archive(n))) File.Move(Archive(n), Archive(n + 1), overwrite: true);
        if (KeepFiles > 1) File.Move(CurrentPath, Archive(1), overwrite: true);
        else File.Delete(CurrentPath);
    }

    internal static string Format(DateTimeOffset time, LogLevel level, string message, Exception? error)
    {
        var text = new StringBuilder()
            .Append(time.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
            .Append(' ').Append(level.ToString().ToUpperInvariant().PadRight(7))
            .Append(' ').Append(message);
        for (var e = error; e is not null; e = e.InnerException)
            text.Append(" | ").Append(e.GetType().Name).Append(": ").Append(e.Message);
        return text.ToString();
    }
}
