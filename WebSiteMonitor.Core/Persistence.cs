using System.Text.Json;

namespace WebSiteMonitor.Core;

public sealed class SettingsStore
{
    private readonly string _path;
    private readonly object _sync = new();
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
        lock (_sync) WriteAtomic(_path, JsonSerializer.SerializeToUtf8Bytes(settings, JsonOptions));
    }

    // Keep the exact original settings bytes in memory until SQLite commits. Nothing is exported
    // in plaintext. A failed write or failed DB commit restores the file as well as the transaction.
    public void SaveCoordinated(AppSettings settings, Action<Action> databaseTransaction)
    {
        lock (_sync)
        {
            var previous = File.Exists(_path) ? File.ReadAllBytes(_path) : null;
            var attempted = false;
            try { databaseTransaction(() => { attempted = true; Save(settings); }); }
            catch
            {
                if (attempted)
                {
                    try
                    {
                        if (previous is null) { if (File.Exists(_path)) File.Delete(_path); }
                        else if (!File.Exists(_path) || !File.ReadAllBytes(_path).AsSpan().SequenceEqual(previous)) WriteAtomic(_path, previous);
                    }
                    catch { throw new ConfigurationException("取り込みを中止しましたが、設定ファイルの復元に失敗しました。保存先の状態を確認してください。アプリを終了せず復旧してください。"); }
                }
                throw;
            }
        }
    }

    public static void WriteAtomic(string path, byte[] bytes)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

public sealed class FileLogger
{
    private readonly string _directory;
    private readonly object _sync = new();
    public FileLogger(string directory) { _directory = directory; try { Directory.CreateDirectory(directory); } catch { } }
    public void Info(string message) => Write("INFO", message);
    public void Error(string message, Exception? exception = null) => Write("ERROR", exception is null ? message : message + " | " + NotificationUrl.SafeException(exception));
    private void Write(string level, string message)
    {
        try
        {
            var safeMessage = NotificationUrl.RedactText(message);
            lock (_sync)
            {
                Directory.CreateDirectory(_directory);
                var path = Path.Combine(_directory, DateTime.Now.ToString("yyyy-MM-dd") + ".log");
                if (File.Exists(path) && new FileInfo(path).Length > 10 * 1024 * 1024) path = Path.Combine(_directory, DateTime.Now.ToString("yyyy-MM-dd-HHmmss") + ".log");
                File.AppendAllText(path, $"{DateTimeOffset.Now:O} [{level}] {safeMessage.ReplaceLineEndings(" ")}\n");
            }
        }
        catch { /* Diagnostics must not interrupt monitoring, including storage failures. */ }
    }
    public void Cleanup(int days) { try { var cutoff = DateTime.Now.AddDays(-Math.Clamp(days,1,3650)); foreach (var f in Directory.EnumerateFiles(_directory,"*.log")) { try { if (File.GetLastWriteTime(f) < cutoff) File.Delete(f); } catch { } } } catch { } }
}
