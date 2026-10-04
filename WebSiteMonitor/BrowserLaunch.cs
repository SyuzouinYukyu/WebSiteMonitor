using System.Diagnostics;
using WebSiteMonitor.Core;

namespace WebSiteMonitor;

internal static class BrowserLaunch
{
    internal static ProcessStartInfo? CreateStartInfo(string url)
    {
        var safeUrl = NotificationUrl.Sanitize(url);
        return safeUrl.Length == 0 ? null : new ProcessStartInfo(safeUrl) { UseShellExecute = true };
    }
}
