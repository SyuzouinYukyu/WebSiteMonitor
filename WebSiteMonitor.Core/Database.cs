using Microsoft.Data.Sqlite;

namespace WebSiteMonitor.Core;

public sealed class Database
{
    private readonly string _connectionString;
    public int SchemaVersion => 4;
    public event EventHandler? SitesChanged;

    public Database(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Private, ForeignKeys = true
        }.ToString();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    public void Initialize()
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS SchemaVersion (Version INTEGER NOT NULL);
                INSERT INTO SchemaVersion(Version) SELECT 1 WHERE NOT EXISTS(SELECT 1 FROM SchemaVersion);
                CREATE TABLE IF NOT EXISTS Sites (
                  Id INTEGER PRIMARY KEY AUTOINCREMENT, Name TEXT NOT NULL, Url TEXT NOT NULL, Enabled INTEGER NOT NULL,
                  MonitorMode INTEGER NOT NULL, FeedUrl TEXT, AutoDetectedFeedUrl TEXT, Selector TEXT, XPath TEXT, Regex TEXT,
                  ScheduleMode INTEGER NOT NULL, IntervalMinutes INTEGER NOT NULL, DailyTime TEXT NOT NULL,
                  WindowsNotification INTEGER NOT NULL, PopupNotification INTEGER NOT NULL, SoundNotification INTEGER NOT NULL,
                  SoundFile TEXT, SoundVolume INTEGER NOT NULL, LastHash TEXT, LastPreview TEXT, LastETag TEXT, LastModified TEXT,
                  LastChecked TEXT, LastChanged TEXT, LastNotifiedHash TEXT, NextDue TEXT, ConsecutiveErrors INTEGER NOT NULL DEFAULT 0,
                  LastError TEXT, EffectiveMode TEXT, MonitorRevision INTEGER NOT NULL DEFAULT 0, UseBrowserCompatibleUserAgent INTEGER NOT NULL DEFAULT 0);
                CREATE TABLE IF NOT EXISTS History (
                  Id INTEGER PRIMARY KEY AUTOINCREMENT, SiteId INTEGER NOT NULL, ChangedAt TEXT NOT NULL,
                  OldHash TEXT, NewHash TEXT NOT NULL, OldPreview TEXT, NewPreview TEXT NOT NULL,
                  FOREIGN KEY(SiteId) REFERENCES Sites(Id) ON DELETE CASCADE);
                CREATE INDEX IF NOT EXISTS IX_Sites_Enabled_NextDue ON Sites(Enabled, NextDue);
                CREATE INDEX IF NOT EXISTS IX_History_SiteId_ChangedAt ON History(SiteId, ChangedAt DESC);
                """;
            command.ExecuteNonQuery();
        }

        var columns = GetSiteColumnNames(connection, transaction);
        if (!columns.Contains("AutoDetectedFeedUrl"))
        {
            Execute(connection, transaction, "ALTER TABLE Sites ADD COLUMN AutoDetectedFeedUrl TEXT;");
            Execute(connection, transaction, """
                UPDATE Sites SET AutoDetectedFeedUrl=FeedUrl, FeedUrl=NULL
                WHERE MonitorMode=$auto AND FeedUrl IS NOT NULL AND length(trim(FeedUrl))>0;
                """, ("$auto", (int)MonitorMode.Auto));
        }
        if (!columns.Contains("MonitorRevision"))
            Execute(connection, transaction, "ALTER TABLE Sites ADD COLUMN MonitorRevision INTEGER NOT NULL DEFAULT 0;");
        if (!columns.Contains("UseBrowserCompatibleUserAgent"))
            Execute(connection, transaction, "ALTER TABLE Sites ADD COLUMN UseBrowserCompatibleUserAgent INTEGER NOT NULL DEFAULT 0;");
        Execute(connection, transaction, "UPDATE SchemaVersion SET Version=4;");
        transaction.Commit();
    }

    public int GetSchemaVersion()
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Version FROM SchemaVersion LIMIT 1";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public List<Site> GetSites()
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM Sites ORDER BY Name COLLATE NOCASE";
        using var r = cmd.ExecuteReader();
        var result = new List<Site>();
        while (r.Read()) result.Add(ReadSite(r));
        return result;
    }

    public Site? GetSite(long id)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM Sites WHERE Id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadSite(r) : null;
    }

    public long SaveSite(Site site)
    {
        SiteValidation.Validate(site);
        if (site.MonitorMode != MonitorMode.Feed) site.FeedUrl = null;
        var now = DateTimeOffset.Now;
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        if (site.Id == 0)
        {
            site.MonitorRevision = Math.Max(0, site.MonitorRevision);
            SetInitialNextDue(site, now);
        }
        else
        {
            var existing = GetSiteForUpdate(connection, transaction, site.Id)
                ?? throw new InvalidOperationException("保存対象のサイトが見つかりません。");
            var monitoringChanged = SiteValidation.MonitoringConfigurationChanged(existing, site);
            if (monitoringChanged)
            {
                site.MonitorRevision = checked(existing.MonitorRevision + 1);
                ResetMonitoringState(site, now);
            }
            else
            {
                site.MonitorRevision = existing.MonitorRevision;
                RecalculateNextDueAfterScheduleChange(existing, site, now);
            }
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = site.Id == 0 ? """
            INSERT INTO Sites(Name,Url,Enabled,MonitorMode,FeedUrl,AutoDetectedFeedUrl,Selector,XPath,Regex,ScheduleMode,IntervalMinutes,DailyTime,WindowsNotification,PopupNotification,SoundNotification,SoundFile,SoundVolume,LastHash,LastPreview,LastETag,LastModified,LastChecked,LastChanged,LastNotifiedHash,NextDue,ConsecutiveErrors,LastError,EffectiveMode,MonitorRevision,UseBrowserCompatibleUserAgent)
            VALUES($Name,$Url,$Enabled,$MonitorMode,$FeedUrl,$AutoDetectedFeedUrl,$Selector,$XPath,$Regex,$ScheduleMode,$IntervalMinutes,$DailyTime,$WindowsNotification,$PopupNotification,$SoundNotification,$SoundFile,$SoundVolume,$LastHash,$LastPreview,$LastETag,$LastModified,$LastChecked,$LastChanged,$LastNotifiedHash,$NextDue,$ConsecutiveErrors,$LastError,$EffectiveMode,$MonitorRevision,$UseBrowserCompatibleUserAgent);
            SELECT last_insert_rowid();
            """ : """
            UPDATE Sites SET Name=$Name,Url=$Url,Enabled=$Enabled,MonitorMode=$MonitorMode,FeedUrl=$FeedUrl,AutoDetectedFeedUrl=$AutoDetectedFeedUrl,Selector=$Selector,XPath=$XPath,Regex=$Regex,ScheduleMode=$ScheduleMode,IntervalMinutes=$IntervalMinutes,DailyTime=$DailyTime,WindowsNotification=$WindowsNotification,PopupNotification=$PopupNotification,SoundNotification=$SoundNotification,SoundFile=$SoundFile,SoundVolume=$SoundVolume,LastHash=$LastHash,LastPreview=$LastPreview,LastETag=$LastETag,LastModified=$LastModified,LastChecked=$LastChecked,LastChanged=$LastChanged,LastNotifiedHash=$LastNotifiedHash,NextDue=$NextDue,ConsecutiveErrors=$ConsecutiveErrors,LastError=$LastError,EffectiveMode=$EffectiveMode,MonitorRevision=$MonitorRevision,UseBrowserCompatibleUserAgent=$UseBrowserCompatibleUserAgent WHERE Id=$Id;
            SELECT $Id;
            """;
        AddSiteParameters(command, site);
        var id = Convert.ToInt64(command.ExecuteScalar());
        transaction.Commit();
        site.Id = id;
        OnSitesChanged();
        return id;
    }

    public static void ResetMonitoringState(Site site, DateTimeOffset now)
    {
        site.AutoDetectedFeedUrl = null;
        site.LastHash = null;
        site.LastPreview = null;
        site.LastETag = null;
        site.LastModified = null;
        site.LastChecked = null;
        site.LastChanged = null;
        site.LastNotifiedHash = null;
        site.ConsecutiveErrors = 0;
        site.LastError = null;
        site.EffectiveMode = null;
        site.NextDue = site.Enabled && site.ScheduleMode != ScheduleMode.Manual ? now : null;
    }

    public void DeleteSite(long id)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM Sites WHERE Id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
        OnSitesChanged();
    }

    public CheckResult ApplySuccess(long siteId, string hash, string preview, string? etag, string? lastModified, MonitorMode effectiveMode, DateTimeOffset now)
    {
        var site = GetSite(siteId) ?? throw new InvalidOperationException("監視サイトが見つかりません。");
        return ApplySuccess(siteId, site.MonitorRevision, hash, preview, etag, lastModified, effectiveMode, now);
    }

    public CheckResult ApplySuccess(long siteId, long expectedRevision, string hash, string preview, string? etag, string? lastModified, MonitorMode effectiveMode, DateTimeOffset now, bool updateAutoDetectedFeed = false, string? autoDetectedFeedUrl = null)
    {
        using var c = Open(); using var tx = c.BeginTransaction();
        var site = GetSiteForUpdate(c, tx, siteId) ?? throw new InvalidOperationException("監視サイトが見つかりません。");
        if (site.MonitorRevision != expectedRevision)
        {
            tx.Rollback();
            return Discarded(site);
        }

        var outcome = UpdateDecision.Decide(site.LastHash, hash);
        var next = ScheduleCalculator.NextDue(WithLastChecked(site, now), now);
        using var update = c.CreateCommand();
        update.Transaction = tx;
        update.CommandText = """
            UPDATE Sites SET LastHash=$hash,LastPreview=$preview,LastETag=$etag,LastModified=$modified,LastChecked=$checked,
              LastChanged=CASE WHEN $changed=1 THEN $checked ELSE LastChanged END,NextDue=$next,ConsecutiveErrors=0,
              LastError=NULL,EffectiveMode=$mode,
              AutoDetectedFeedUrl=CASE WHEN $setAuto=1 THEN $autoFeed ELSE AutoDetectedFeedUrl END
            WHERE Id=$id AND MonitorRevision=$revision;
            """;
        update.Parameters.AddWithValue("$hash", hash);
        update.Parameters.AddWithValue("$preview", preview);
        update.Parameters.AddWithValue("$etag", Db(etag));
        update.Parameters.AddWithValue("$modified", Db(lastModified));
        update.Parameters.AddWithValue("$checked", now.ToString("O"));
        update.Parameters.AddWithValue("$changed", outcome == CheckOutcome.Changed ? 1 : 0);
        update.Parameters.AddWithValue("$next", Db(next?.ToString("O")));
        update.Parameters.AddWithValue("$mode", effectiveMode.ToString());
        update.Parameters.AddWithValue("$setAuto", updateAutoDetectedFeed ? 1 : 0);
        update.Parameters.AddWithValue("$autoFeed", Db(autoDetectedFeedUrl));
        update.Parameters.AddWithValue("$id", siteId);
        update.Parameters.AddWithValue("$revision", expectedRevision);
        if (update.ExecuteNonQuery() != 1)
        {
            tx.Rollback();
            return Discarded(GetSite(siteId) ?? site);
        }

        if (outcome == CheckOutcome.Changed)
        {
            using var history = c.CreateCommand();
            history.Transaction = tx;
            history.CommandText = "INSERT INTO History(SiteId,ChangedAt,OldHash,NewHash,OldPreview,NewPreview) VALUES($id,$at,$old,$new,$op,$np)";
            history.Parameters.AddWithValue("$id", siteId);
            history.Parameters.AddWithValue("$at", now.ToString("O"));
            history.Parameters.AddWithValue("$old", Db(site.LastHash));
            history.Parameters.AddWithValue("$new", hash);
            history.Parameters.AddWithValue("$op", Db(site.LastPreview));
            history.Parameters.AddWithValue("$np", preview);
            history.ExecuteNonQuery();
        }
        tx.Commit();

        site.LastHash = hash;
        site.LastPreview = preview;
        site.LastETag = etag;
        site.LastModified = lastModified;
        site.LastChecked = now;
        site.NextDue = next;
        site.EffectiveMode = effectiveMode.ToString();
        if (updateAutoDetectedFeed) site.AutoDetectedFeedUrl = autoDetectedFeedUrl;
        if (outcome == CheckOutcome.Changed) site.LastChanged = now;
        var notify = UpdateDecision.ShouldNotify(site.LastNotifiedHash, hash, outcome);
        return new CheckResult(outcome, site, outcome switch
        {
            CheckOutcome.BaselineCreated => "初回基準を保存しました",
            CheckOutcome.Changed => "更新を検出しました",
            _ => "更新なし"
        }, hash, preview, notify, expectedRevision);
    }

    public bool ApplyNotModified(long siteId, long expectedRevision, DateTimeOffset now)
    {
        using var c = Open(); using var tx = c.BeginTransaction();
        var site = GetSiteForUpdate(c, tx, siteId) ?? throw new InvalidOperationException("監視サイトが見つかりません。");
        if (site.MonitorRevision != expectedRevision) { tx.Rollback(); return false; }
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE Sites SET LastChecked=$at,NextDue=$next,ConsecutiveErrors=0,LastError=NULL WHERE Id=$id AND MonitorRevision=$revision";
        cmd.Parameters.AddWithValue("$at", now.ToString("O"));
        cmd.Parameters.AddWithValue("$next", Db(ScheduleCalculator.NextDue(WithLastChecked(site, now), now)?.ToString("O")));
        cmd.Parameters.AddWithValue("$id", siteId);
        cmd.Parameters.AddWithValue("$revision", expectedRevision);
        var applied = cmd.ExecuteNonQuery() == 1;
        if (applied) tx.Commit(); else tx.Rollback();
        return applied;
    }

    public void ApplyNotModified(long siteId, DateTimeOffset now)
    {
        var site = GetSite(siteId) ?? throw new InvalidOperationException("監視サイトが見つかりません。");
        ApplyNotModified(siteId, site.MonitorRevision, now);
    }

    public bool ApplyError(long siteId, long expectedRevision, string error, DateTimeOffset now)
    {
        using var c = Open(); using var tx = c.BeginTransaction();
        var site = GetSiteForUpdate(c, tx, siteId) ?? throw new InvalidOperationException("監視サイトが見つかりません。");
        if (site.MonitorRevision != expectedRevision) { tx.Rollback(); return false; }
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE Sites SET LastChecked=$at,NextDue=$next,ConsecutiveErrors=ConsecutiveErrors+1,LastError=$error WHERE Id=$id AND MonitorRevision=$revision";
        cmd.Parameters.AddWithValue("$at", now.ToString("O"));
        cmd.Parameters.AddWithValue("$next", Db(ScheduleCalculator.NextDue(WithLastChecked(site, now), now)?.ToString("O")));
        cmd.Parameters.AddWithValue("$error", ContentHasher.Preview(error, 2000));
        cmd.Parameters.AddWithValue("$id", siteId);
        cmd.Parameters.AddWithValue("$revision", expectedRevision);
        var applied = cmd.ExecuteNonQuery() == 1;
        if (applied) tx.Commit(); else tx.Rollback();
        return applied;
    }

    public void ApplyError(long siteId, string error, DateTimeOffset now)
    {
        var site = GetSite(siteId) ?? throw new InvalidOperationException("監視サイトが見つかりません。");
        ApplyError(siteId, site.MonitorRevision, error, now);
    }

    public bool MarkNotified(long siteId, long expectedRevision, string hash)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE Sites SET LastNotifiedHash=$hash WHERE Id=$id AND MonitorRevision=$revision AND LastHash=$hash";
        cmd.Parameters.AddWithValue("$hash", hash);
        cmd.Parameters.AddWithValue("$id", siteId);
        cmd.Parameters.AddWithValue("$revision", expectedRevision);
        return cmd.ExecuteNonQuery() == 1;
    }

    public void MarkNotified(long siteId, string hash)
    {
        var site = GetSite(siteId) ?? throw new InvalidOperationException("監視サイトが見つかりません。");
        MarkNotified(siteId, site.MonitorRevision, hash);
    }

    public bool SaveDetectedFeed(long siteId, long expectedRevision, string? feedUrl)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE Sites SET AutoDetectedFeedUrl=$feed WHERE Id=$id AND MonitorRevision=$revision";
        cmd.Parameters.AddWithValue("$feed", Db(feedUrl));
        cmd.Parameters.AddWithValue("$id", siteId);
        cmd.Parameters.AddWithValue("$revision", expectedRevision);
        return cmd.ExecuteNonQuery() == 1;
    }

    public void SaveDetectedFeed(long siteId, string? feedUrl)
    {
        var site = GetSite(siteId) ?? throw new InvalidOperationException("監視サイトが見つかりません。");
        SaveDetectedFeed(siteId, site.MonitorRevision, feedUrl);
    }

    public List<HistoryEntry> GetHistory(long? siteId = null, int limit = 1000)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT h.Id,h.SiteId,s.Name,h.ChangedAt,h.OldHash,h.NewHash,h.OldPreview,h.NewPreview,s.Url FROM History h JOIN Sites s ON s.Id=h.SiteId WHERE ($site IS NULL OR h.SiteId=$site) ORDER BY h.ChangedAt DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$site", siteId is null ? DBNull.Value : siteId.Value);
        cmd.Parameters.AddWithValue("$limit", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<HistoryEntry>();
        while (r.Read()) list.Add(new(r.GetInt64(0), r.GetInt64(1), r.GetString(2), DateTimeOffset.Parse(r.GetString(3)), NullableString(r, 4), r.GetString(5), NullableString(r, 6), r.GetString(7), r.GetString(8)));
        return list;
    }

    public int CleanupHistory(int retentionDays, DateTimeOffset now)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM History WHERE ChangedAt < $cutoff";
        cmd.Parameters.AddWithValue("$cutoff", now.AddDays(-Math.Clamp(retentionDays, 1, 3650)).ToString("O"));
        return cmd.ExecuteNonQuery();
    }

    private static void SetInitialNextDue(Site site, DateTimeOffset now)
    {
        site.NextDue = site.Enabled && site.ScheduleMode != ScheduleMode.Manual ? now : null;
    }

    private static void RecalculateNextDueAfterScheduleChange(Site previous, Site current, DateTimeOffset now)
    {
        if (!SiteValidation.SchedulingConfigurationChanged(previous, current)) return;
        if (!current.Enabled || current.ScheduleMode == ScheduleMode.Manual) { current.NextDue = null; return; }
        if (!previous.Enabled && current.Enabled) { current.NextDue = now; return; }
        current.NextDue = current.ScheduleMode switch
        {
            ScheduleMode.Interval => now.AddMinutes(Math.Clamp(current.IntervalMinutes, 5, 10080)),
            ScheduleMode.Daily => ScheduleCalculator.NextDue(current, now),
            _ => null
        };
    }

    private static CheckResult Discarded(Site current) => new(CheckOutcome.Discarded, current, "監視設定が変更されたため取得結果を破棄しました", null, null, false, current.MonitorRevision);

    private static HashSet<string> GetSiteColumnNames(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA table_info(Sites)";
        using var reader = command.ExecuteReader();
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read()) result.Add(reader.GetString(1));
        return result;
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object Value)[] values)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value);
        command.ExecuteNonQuery();
    }

    private void OnSitesChanged() => SitesChanged?.Invoke(this, EventArgs.Empty);
    private static Site? GetSiteForUpdate(SqliteConnection connection, SqliteTransaction transaction, long id)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT * FROM Sites WHERE Id=$id";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadSite(reader) : null;
    }

    private static Site WithLastChecked(Site s, DateTimeOffset value) { s.LastChecked = value; return s; }
    private static object Db(string? value) => value is null ? DBNull.Value : value;
    private static string? NullableString(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static DateTimeOffset? Date(SqliteDataReader r, string name) => r.IsDBNull(r.GetOrdinal(name)) ? null : DateTimeOffset.Parse(r.GetString(r.GetOrdinal(name)));

    private static Site ReadSite(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(r.GetOrdinal("Id")),
        MonitorRevision = r.GetInt64(r.GetOrdinal("MonitorRevision")),
        UseBrowserCompatibleUserAgent = r.GetBoolean(r.GetOrdinal("UseBrowserCompatibleUserAgent")),
        Name = r.GetString(r.GetOrdinal("Name")),
        Url = r.GetString(r.GetOrdinal("Url")),
        Enabled = r.GetBoolean(r.GetOrdinal("Enabled")),
        MonitorMode = (MonitorMode)r.GetInt32(r.GetOrdinal("MonitorMode")),
        FeedUrl = NullableString(r, r.GetOrdinal("FeedUrl")),
        AutoDetectedFeedUrl = NullableString(r, r.GetOrdinal("AutoDetectedFeedUrl")),
        Selector = NullableString(r, r.GetOrdinal("Selector")),
        XPath = NullableString(r, r.GetOrdinal("XPath")),
        Regex = NullableString(r, r.GetOrdinal("Regex")),
        ScheduleMode = (ScheduleMode)r.GetInt32(r.GetOrdinal("ScheduleMode")),
        IntervalMinutes = r.GetInt32(r.GetOrdinal("IntervalMinutes")),
        DailyTime = r.GetString(r.GetOrdinal("DailyTime")),
        WindowsNotification = r.GetBoolean(r.GetOrdinal("WindowsNotification")),
        PopupNotification = r.GetBoolean(r.GetOrdinal("PopupNotification")),
        SoundNotification = r.GetBoolean(r.GetOrdinal("SoundNotification")),
        SoundFile = NullableString(r, r.GetOrdinal("SoundFile")),
        SoundVolume = r.GetInt32(r.GetOrdinal("SoundVolume")),
        LastHash = NullableString(r, r.GetOrdinal("LastHash")),
        LastPreview = NullableString(r, r.GetOrdinal("LastPreview")),
        LastETag = NullableString(r, r.GetOrdinal("LastETag")),
        LastModified = NullableString(r, r.GetOrdinal("LastModified")),
        LastChecked = Date(r, "LastChecked"),
        LastChanged = Date(r, "LastChanged"),
        LastNotifiedHash = NullableString(r, r.GetOrdinal("LastNotifiedHash")),
        NextDue = Date(r, "NextDue"),
        ConsecutiveErrors = r.GetInt32(r.GetOrdinal("ConsecutiveErrors")),
        LastError = NullableString(r, r.GetOrdinal("LastError")),
        EffectiveMode = NullableString(r, r.GetOrdinal("EffectiveMode") )
    };

    private static void AddSiteParameters(SqliteCommand cmd, Site s)
    {
        var values = new Dictionary<string, object?>
        {
            ["Id"] = s.Id, ["Name"] = s.Name, ["Url"] = s.Url, ["Enabled"] = s.Enabled,
            ["MonitorMode"] = (int)s.MonitorMode, ["FeedUrl"] = s.FeedUrl, ["AutoDetectedFeedUrl"] = s.AutoDetectedFeedUrl,
            ["Selector"] = s.Selector, ["XPath"] = s.XPath, ["Regex"] = s.Regex, ["ScheduleMode"] = (int)s.ScheduleMode,
            ["IntervalMinutes"] = s.IntervalMinutes, ["DailyTime"] = s.DailyTime, ["WindowsNotification"] = s.WindowsNotification,
            ["PopupNotification"] = s.PopupNotification, ["SoundNotification"] = s.SoundNotification, ["SoundFile"] = s.SoundFile,
            ["SoundVolume"] = s.SoundVolume, ["LastHash"] = s.LastHash, ["LastPreview"] = s.LastPreview,
            ["LastETag"] = s.LastETag, ["LastModified"] = s.LastModified, ["LastChecked"] = s.LastChecked?.ToString("O"),
            ["LastChanged"] = s.LastChanged?.ToString("O"), ["LastNotifiedHash"] = s.LastNotifiedHash,
            ["NextDue"] = s.NextDue?.ToString("O"), ["ConsecutiveErrors"] = s.ConsecutiveErrors,
            ["LastError"] = s.LastError, ["EffectiveMode"] = s.EffectiveMode, ["MonitorRevision"] = s.MonitorRevision,
            ["UseBrowserCompatibleUserAgent"] = s.UseBrowserCompatibleUserAgent,
        };
        foreach (var pair in values) cmd.Parameters.AddWithValue("$" + pair.Key, pair.Value ?? DBNull.Value);
    }
}