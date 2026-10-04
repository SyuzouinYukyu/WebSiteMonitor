using System.Diagnostics;

namespace WebSiteMonitor;

internal static class LogFileLaunch
{
    internal const string NoLog = "開くログファイルがありません。";
    internal const string Failed = "ログファイルを開けませんでした。";

    internal static string? FindLatest(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(Path.GetFullPath(directory), "*", SearchOption.TopDirectoryOnly)
                .Where(path => string.Equals(Path.GetExtension(path), ".log", StringComparison.OrdinalIgnoreCase))
                .Select(path => new FileInfo(path))
                // Do not follow directory links or launch a substituted link target.
                .Where(file => (file.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ThenBy(file => file.Name, StringComparer.Ordinal)
                .Select(file => file.FullName).FirstOrDefault();
        }
        catch (DirectoryNotFoundException) { return null; }
    }

    internal static void OpenLatest(string directory, Action<string> showMessage, Func<ProcessStartInfo, Process?>? start = null)
    {
        string? error = null;
        try
        {
            var path = FindLatest(directory);
            if (path is null) error = NoLog;
            else if (!File.Exists(path)) error = Failed;
            else
            {
                var info = new ProcessStartInfo { FileName = path, UseShellExecute = true };
                // Shell reuse may return null; successful handoff is not an error.
                using var process = (start ?? Process.Start)(info);
            }
        }
        catch { error = Failed; }
        if (error is not null) showMessage(error);
    }
}
