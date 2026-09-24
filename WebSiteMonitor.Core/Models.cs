namespace WebSiteMonitor.Core;

public enum MonitorMode { Auto, Feed, FullPage, Text, CssSelector, XPath, Regex }
public enum ScheduleMode { Interval, Daily, Manual }
public enum CheckOutcome { BaselineCreated, Unchanged, Changed, NotModified, Discarded, Failed }

public sealed class Site
{
    public long Id { get; set; }
    /// <summary>Incremented when the monitoring target changes; completed checks write only if this value still matches.</summary>
    public long MonitorRevision { get; set; }
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public bool UseBrowserCompatibleUserAgent { get; set; }
    public bool Enabled { get; set; } = true;
    public MonitorMode MonitorMode { get; set; } = MonitorMode.Auto;
    public string? FeedUrl { get; set; }
    public string? AutoDetectedFeedUrl { get; set; }
    public string? Selector { get; set; }
    public string? XPath { get; set; }
    public string? Regex { get; set; }
    public ScheduleMode ScheduleMode { get; set; } = ScheduleMode.Interval;
    public int IntervalMinutes { get; set; } = 60;
    public string DailyTime { get; set; } = "09:00";
    public bool WindowsNotification { get; set; } = true;
    public bool PopupNotification { get; set; }
    public bool SoundNotification { get; set; }
    public string? SoundFile { get; set; }
    public int SoundVolume { get; set; } = 80;
    public string? LastHash { get; set; }
    public string? LastPreview { get; set; }
    public string? LastETag { get; set; }
    public string? LastModified { get; set; }
    public DateTimeOffset? LastChecked { get; set; }
    public DateTimeOffset? LastChanged { get; set; }
    public string? LastNotifiedHash { get; set; }
    public DateTimeOffset? NextDue { get; set; }
    public int ConsecutiveErrors { get; set; }
    public string? LastError { get; set; }
    public string? EffectiveMode { get; set; }
}

public sealed record HistoryEntry(long Id, long SiteId, string SiteName, DateTimeOffset ChangedAt,
    string? OldHash, string NewHash, string? OldPreview, string NewPreview, string Url);

public sealed record ExtractionResult(string Content, MonitorMode EffectiveMode, string? DetectedFeedUrl = null);
public sealed record HttpFetchResult(int StatusCode, byte[]? Body, string? MediaType, string? CharacterSet,
    string? ETag, string? LastModified, Uri FinalUri, bool NotModified = false);
public sealed record CheckResult(CheckOutcome Outcome, Site Site, string Message, string? NewHash = null,
    string? NewPreview = null, bool ShouldNotify = false, long MonitorRevision = 0);

public sealed class AppSettings
{
    public double UiFontSize { get; set; } = 10.0;
    public int WindowX { get; set; } = -1;
    public int WindowY { get; set; } = -1;
    public int WindowWidth { get; set; } = 1180;
    public int WindowHeight { get; set; } = 720;
    public Dictionary<string, int> ColumnWidths { get; set; } = [];
    public bool StartWithWindows { get; set; }
    public bool WindowsIntegrationEnabled { get; set; } = true;
    public bool NotificationsEnabled { get; set; } = true;
    public bool DefaultPopup { get; set; }
    public int HistoryRetentionDays { get; set; } = 180;
    public int LogRetentionDays { get; set; } = 30;
    public int PcmSampleRate { get; set; } = 44100;
    public int PcmBits { get; set; } = 16;
    public int PcmChannels { get; set; } = 2;
}

public static class NotificationPolicy
{
    public static bool ShouldDispatch(bool masterEnabled, bool siteChannelEnabled) => masterEnabled && siteChannelEnabled;
}

public static class SiteValidation
{
    public static void Validate(Site site)
    {
        if (string.IsNullOrWhiteSpace(site.Name)) throw new ArgumentException("サイト名を入力してください。");
        ValidateHttpUrl(site.Url, "URL");
        if (!string.IsNullOrWhiteSpace(site.FeedUrl)) ValidateHttpUrl(site.FeedUrl, "RSS / Atom URL");
        if (site.IntervalMinutes is < 5 or > 10080) throw new ArgumentException("確認間隔は5～10080分で指定してください。");
        if (!TimeOnly.TryParseExact(site.DailyTime, "HH:mm", out _)) throw new ArgumentException("毎日の時刻は HH:mm 形式で指定してください。");
        if (site.SoundVolume is < 0 or > 100) throw new ArgumentException("音量は0～100で指定してください。");
        if (site.MonitorMode == MonitorMode.CssSelector && string.IsNullOrWhiteSpace(site.Selector)) throw new ArgumentException("CSS Selectorを入力してください。");
        if (site.MonitorMode == MonitorMode.XPath && string.IsNullOrWhiteSpace(site.XPath)) throw new ArgumentException("XPathを入力してください。");
        if (site.MonitorMode == MonitorMode.Regex && string.IsNullOrWhiteSpace(site.Regex)) throw new ArgumentException("正規表現を入力してください。");
        ValidateMonitoringSyntax(site);
    }

    public static void ValidateMonitoringSyntax(Site site)
    {
        switch (site.MonitorMode)
        {
            case MonitorMode.CssSelector: ContentExtractor.ValidateCssSelector(site.Selector!); break;
            case MonitorMode.XPath: ContentExtractor.ValidateXPath(site.XPath!); break;
            case MonitorMode.Regex: ContentExtractor.ValidateRegex(site.Regex!); break;
        }
    }

    public static bool MonitoringConfigurationChanged(Site previous, Site current)
        => previous.UseBrowserCompatibleUserAgent != current.UseBrowserCompatibleUserAgent
           || !string.Equals(previous.Url, current.Url, StringComparison.Ordinal)
           || previous.MonitorMode != current.MonitorMode
           || !string.Equals(previous.FeedUrl, current.FeedUrl, StringComparison.Ordinal)
           || !string.Equals(previous.Selector, current.Selector, StringComparison.Ordinal)
           || !string.Equals(previous.XPath, current.XPath, StringComparison.Ordinal)
           || !string.Equals(previous.Regex, current.Regex, StringComparison.Ordinal);

    public static bool SchedulingConfigurationChanged(Site previous, Site current)
        => previous.ScheduleMode != current.ScheduleMode
           || previous.IntervalMinutes != current.IntervalMinutes
           || !string.Equals(previous.DailyTime, current.DailyTime, StringComparison.Ordinal)
           || previous.Enabled != current.Enabled;

    public static Uri ValidateHttpUrl(string? value, string label = "URL")
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException($"{label}は http または https のURLを指定してください。");
        return uri;
    }
}