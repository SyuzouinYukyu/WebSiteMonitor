using System.Text.Json;

namespace WebSiteMonitor.Core;

public sealed class SettingsStore
{
    private readonly string _path;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public string? RecoveryMessage { get; private set; }
    public SettingsStore(string path) => _path = path;

    public AppSettings Load()
    {
        if (!File.Exists(_path)) return new();
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), JsonOptions) ?? new();
            settings.UiFontSize = double.IsFinite(settings.UiFontSize) ? Math.Clamp(settings.UiFontSize, 10.0, 18.0) : 10.0;
            return settings;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            var backup = _path + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            try { File.Copy(_path, backup, false); RecoveryMessage = $"設定ファイル破損のため初期値へ復旧しました。退避先: {backup}"; } catch { RecoveryMessage = "設定ファイル破損のため初期値へ復旧しました。"; }
            return new();
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(settings, JsonOptions);
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
        File.Move(temp, _path, true);
    }
}

public sealed class FileLogger
{
    private readonly string _directory;
    private readonly object _sync = new();
    public FileLogger(string directory) { _directory = directory; Directory.CreateDirectory(directory); }
    public void Info(string message) => Write("INFO", message);
    public void Error(string message, Exception? exception = null) => Write("ERROR", exception is null ? message : message + " | " + exception.GetType().Name + ": " + exception.Message);
    private void Write(string level, string message)
    {
        lock (_sync)
        {
            Directory.CreateDirectory(_directory);
            var path = Path.Combine(_directory, DateTime.Now.ToString("yyyy-MM-dd") + ".log");
            if (File.Exists(path) && new FileInfo(path).Length > 10 * 1024 * 1024) path = Path.Combine(_directory, DateTime.Now.ToString("yyyy-MM-dd-HHmmss") + ".log");
            File.AppendAllText(path, $"{DateTimeOffset.Now:O} [{level}] {message.ReplaceLineEndings(" ")}
");
        }
    }
    public void Cleanup(int days) { var cutoff = DateTime.Now.AddDays(-Math.Clamp(days,1,3650)); foreach (var f in Directory.EnumerateFiles(_directory,"*.log")) { try { if (File.GetLastWriteTime(f) < cutoff) File.Delete(f); } catch { } } }
}