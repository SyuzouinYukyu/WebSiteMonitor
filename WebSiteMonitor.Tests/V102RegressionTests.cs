using System.Text;
using Microsoft.Data.Sqlite;
using WebSiteMonitor.Core;

namespace WebSiteMonitor.Tests;

public sealed class V102RegressionTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "WebSiteMonitorV102Tests-" + Guid.NewGuid().ToString("N"));

    public V102RegressionTests() => Directory.CreateDirectory(_temp);
    public void Dispose() { try { Directory.Delete(_temp, true); } catch { } }

    [Fact]
    public void V101DatabaseMigratesToSchema3WithoutLosingSiteOrHistory()
    {
        var path = Path.Combine(_temp, "v101.db");
        CreateV101Database(path);
        var database = new Database(path);
        database.Initialize();
        database.Initialize();
        var site = Assert.Single(database.GetSites());
        Assert.Equal(7, database.GetSchemaVersion());
        Assert.Equal(0, site.MonitorRevision);
        Assert.Equal("before", site.LastHash);
        Assert.Single(database.GetHistory());
    }

    [Theory]
    [InlineData("url")]
    [InlineData("mode")]
    [InlineData("selector")]
    [InlineData("ua")]
    public async Task ChangedMonitoringConfigurationDiscardsInFlightResult(string kind)
    {
        var database = NewDatabase();
        var site = ValidSite();
        if (kind == "selector") { site.MonitorMode = MonitorMode.CssSelector; site.Selector = ".old"; }
        database.SaveSite(site);
        database.ApplySuccess(site.Id, "baseline", "baseline", null, null, site.MonitorMode, DateTimeOffset.Now);
        var fetcher = new BlockingFetcher();
        var engine = new MonitorEngine(database, fetcher, new FileLogger(Path.Combine(_temp, "logs")));
        var check = engine.CheckAsync(database.GetSite(site.Id)!, CancellationToken.None);
        await fetcher.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var edited = database.GetSite(site.Id)!;
        switch (kind)
        {
            case "url": edited.Url = "https://example.test/replaced"; break;
            case "mode": edited.MonitorMode = MonitorMode.FullPage; break;
            case "selector": edited.Selector = ".new"; break;
            case "ua": edited.UseBrowserCompatibleUserAgent = true; break;
        }
        database.SaveSite(edited);
        fetcher.Release("<html><body><div class='old'>stale result</div></body></html>");

        var result = await check;
        var stored = database.GetSite(site.Id)!;
        Assert.Equal(CheckOutcome.Discarded, result.Outcome);
        Assert.False(result.ShouldNotify);
        Assert.Equal(1, stored.MonitorRevision);
        Assert.Null(stored.LastHash);
        Assert.Null(stored.LastPreview);
        Assert.Empty(database.GetHistory());
    }

    [Fact]
    public void RevisionMismatchDoesNotWriteNotificationState()
    {
        var database = NewDatabase();
        var site = ValidSite();
        database.SaveSite(site);
        database.ApplySuccess(site.Id, "a", "a", null, null, MonitorMode.Text, DateTimeOffset.Now);
        var oldRevision = database.GetSite(site.Id)!.MonitorRevision;
        var edited = database.GetSite(site.Id)!;
        edited.Url = "https://example.test/new";
        database.SaveSite(edited);
        Assert.False(database.MarkNotified(site.Id, oldRevision, "a"));
        Assert.Null(database.GetSite(site.Id)!.LastNotifiedHash);
    }

    [Fact]
    public void IntervalChangeFromSevenDaysToFiveMinutesUsesNewInterval()
    {
        var database = NewDatabase();
        var site = ValidSite(); site.IntervalMinutes = 10080; database.SaveSite(site);
        var edit = database.GetSite(site.Id)!; edit.IntervalMinutes = 5;
        var before = DateTimeOffset.Now; database.SaveSite(edit); var after = DateTimeOffset.Now;
        Assert.InRange(database.GetSite(site.Id)!.NextDue!.Value, before.AddMinutes(5), after.AddMinutes(5));
    }

    [Fact]
    public void IntervalChangeFromFiveMinutesToSevenDaysUsesNewInterval()
    {
        var database = NewDatabase();
        var site = ValidSite(); site.IntervalMinutes = 5; database.SaveSite(site);
        var edit = database.GetSite(site.Id)!; edit.IntervalMinutes = 10080;
        var before = DateTimeOffset.Now; database.SaveSite(edit); var after = DateTimeOffset.Now;
        Assert.InRange(database.GetSite(site.Id)!.NextDue!.Value, before.AddMinutes(10080), after.AddMinutes(10080));
    }

    [Fact]
    public void DailyChangeUsesTheNearestNewDailyTime()
    {
        var database = NewDatabase();
        var site = ValidSite(); site.ScheduleMode = ScheduleMode.Daily; site.DailyTime = "01:00"; database.SaveSite(site);
        var edit = database.GetSite(site.Id)!; edit.DailyTime = DateTime.Now.AddHours(2).ToString("HH:mm"); database.SaveSite(edit);
        Assert.Equal(edit.DailyTime, database.GetSite(site.Id)!.NextDue!.Value.ToLocalTime().ToString("HH:mm"));
    }

    [Fact]
    public void DisableClearsNextDueAndEnableSchedulesImmediateCheck()
    {
        var database = NewDatabase(); var site = ValidSite(); database.SaveSite(site);
        var disabled = database.GetSite(site.Id)!; disabled.Enabled = false; database.SaveSite(disabled);
        Assert.Null(database.GetSite(site.Id)!.NextDue);
        var enabled = database.GetSite(site.Id)!; enabled.Enabled = true;
        var before = DateTimeOffset.Now; database.SaveSite(enabled); var after = DateTimeOffset.Now;
        Assert.InRange(database.GetSite(site.Id)!.NextDue!.Value, before, after);
    }

    [Fact]
    public void MonitoringChangeSchedulesImmediateBaselineAndManualHasNoAutomaticDue()
    {
        var database = NewDatabase(); var site = ValidSite(); database.SaveSite(site);
        var edit = database.GetSite(site.Id)!; edit.Url = "https://example.test/new";
        var before = DateTimeOffset.Now; database.SaveSite(edit); var after = DateTimeOffset.Now;
        Assert.InRange(database.GetSite(site.Id)!.NextDue!.Value, before, after);
        edit = database.GetSite(site.Id)!; edit.ScheduleMode = ScheduleMode.Manual; database.SaveSite(edit);
        Assert.Null(database.GetSite(site.Id)!.NextDue);
    }

    [Fact]
    public void SleepRecoveryKeepsAnOverdueIntervalDueImmediately()
    {
        var now = DateTimeOffset.Now;
        var site = ValidSite(); site.LastChecked = now.AddHours(-2); site.IntervalMinutes = 5;
        Assert.Equal(now, ScheduleCalculator.NextDue(site, now));
    }

    [Fact]
    public void ThirdPartyNoticeHasNoForbiddenControlCharacters()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "THIRD_PARTY_NOTICES.md");
        var text = File.ReadAllText(path, new UTF8Encoding(false, true));
        Assert.DoesNotContain(text, ch => char.IsControl(ch) && ch is not '\r' and not '\n' and not '\t');
        Assert.Contains("FFmpeg (external optional tool, not distributed)", text);
        Assert.Contains("not included in this application or its source distribution", text);
        Assert.DoesNotContain("ffmpeg-master-latest-win64-lgpl-shared.zip", text);
        Assert.DoesNotContain("40633DAB97D235F7DE4FF5B8E34E80D778D4E89F97EFB142B081127F3D7C8633", text);
    }

    private Database NewDatabase()
    {
        var database = new Database(Path.Combine(_temp, Guid.NewGuid().ToString("N") + ".db"));
        database.Initialize();
        return database;
    }

    private static Site ValidSite() => new()
    {
        Name = "Test", Url = "https://example.test/", MonitorMode = MonitorMode.Text,
        ScheduleMode = ScheduleMode.Interval, IntervalMinutes = 60, DailyTime = "09:00"
    };

    private static void CreateV101Database(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE SchemaVersion (Version INTEGER NOT NULL);
            INSERT INTO SchemaVersion VALUES(2);
            CREATE TABLE Sites (
              Id INTEGER PRIMARY KEY AUTOINCREMENT, Name TEXT NOT NULL, Url TEXT NOT NULL, Enabled INTEGER NOT NULL,
              MonitorMode INTEGER NOT NULL, FeedUrl TEXT, AutoDetectedFeedUrl TEXT, Selector TEXT, XPath TEXT, Regex TEXT,
              ScheduleMode INTEGER NOT NULL, IntervalMinutes INTEGER NOT NULL, DailyTime TEXT NOT NULL,
              WindowsNotification INTEGER NOT NULL, PopupNotification INTEGER NOT NULL, SoundNotification INTEGER NOT NULL,
              SoundFile TEXT, SoundVolume INTEGER NOT NULL, LastHash TEXT, LastPreview TEXT, LastETag TEXT, LastModified TEXT,
              LastChecked TEXT, LastChanged TEXT, LastNotifiedHash TEXT, NextDue TEXT, ConsecutiveErrors INTEGER NOT NULL DEFAULT 0,
              LastError TEXT, EffectiveMode TEXT);
            CREATE TABLE History (Id INTEGER PRIMARY KEY AUTOINCREMENT, SiteId INTEGER NOT NULL, ChangedAt TEXT NOT NULL, OldHash TEXT, NewHash TEXT NOT NULL, OldPreview TEXT, NewPreview TEXT NOT NULL);
            INSERT INTO Sites(Name,Url,Enabled,MonitorMode,ScheduleMode,IntervalMinutes,DailyTime,WindowsNotification,PopupNotification,SoundNotification,SoundVolume,LastHash,LastPreview,ConsecutiveErrors)
              VALUES('old','https://example.test/',1,3,0,60,'09:00',1,0,0,80,'before','before',0);
            INSERT INTO History(SiteId,ChangedAt,NewHash,NewPreview) VALUES(1,'2026-09-16T00:00:00.0000000+00:00','after','after');
            """;
        command.ExecuteNonQuery();
    }

    private sealed class BlockingFetcher : IHttpFetcher
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource<HttpFetchResult> Response { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<HttpFetchResult> FetchAsync(Uri uri, string? etag, string? lastModified, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            return Response.Task.WaitAsync(cancellationToken);
        }
        public void Release(string html) => Response.TrySetResult(new HttpFetchResult(200, Encoding.UTF8.GetBytes(html), "text/html", "utf-8", null, null, new Uri("https://example.test/")));
    }
}
