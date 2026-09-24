using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;
using Microsoft.Win32;
using WebSiteMonitor.Core;

namespace WebSiteMonitor;

internal static class WindowsIntegration
{
    public const string AppId = "WebSiteMonitor.App";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    public static void Initialize(string exePath, FileLogger logger)
    {
        try
        {
            SetCurrentProcessExplicitAppUserModelID(AppId);
            using var appId = Registry.CurrentUser.CreateSubKey(@"Software\Classes\AppUserModelId\" + AppId);
            appId?.SetValue("DisplayName", "WebSiteMonitor"); appId?.SetValue("IconUri", exePath);
            CreateShortcut(exePath);
        }
        catch (Exception ex) { logger.Error("Windows通知統合の登録に失敗しました", ex); }
    }

    private static void CreateShortcut(string exePath)
    {
        var shortcutPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "WebSiteMonitor.lnk");
        ShortcutInstaller.Create(exePath, shortcutPath, AppId);
    }

    public static void SetAutoStart(bool enabled, string exePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
        if (enabled) key?.SetValue("WebSiteMonitor", $"\"{exePath}\" --autostart"); else key?.DeleteValue("WebSiteMonitor", false);
    }

    public static void Remove(string exePath)
    {
        SetAutoStart(false, exePath);
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\AppUserModelId\" + AppId, false);
        var shortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "WebSiteMonitor.lnk"); if (File.Exists(shortcut)) File.Delete(shortcut);
    }
}

internal sealed class NotificationService
{
    private readonly FileLogger _logger;
    public NotificationService(FileLogger logger)
    {
        _logger = logger;
    }

    public void Show(Site site)
    {
        try
        {
            var title = SecurityElement.Escape("Webサイトが更新されました");
            var name = SecurityElement.Escape(site.Name);
            var message = SecurityElement.Escape($"変更を検出しました  {DateTime.Now:yyyy/MM/dd HH:mm}");
            var launch = SecurityElement.Escape(site.Url);
            var xml = new XmlDocument();
            xml.LoadXml($"<toast activationType=\"protocol\" launch=\"{launch}\"><visual><binding template=\"ToastGeneric\"><text>{title}</text><text>{name}</text><text>{message}</text></binding></visual><audio silent=\"true\"/></toast>");
            ToastNotificationManager.CreateToastNotifier(WindowsIntegration.AppId).Show(new ToastNotification(xml));
        }
        catch (Exception ex) { _logger.Error($"Windows通知に失敗しました SiteId={site.Id}", ex); }
    }
}