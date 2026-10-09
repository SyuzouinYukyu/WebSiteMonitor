using Microsoft.Data.Sqlite;

namespace WebSiteMonitor.Core;

public sealed class Database
{
    private readonly string _connectionString;
    private readonly string _databasePath;
    public string? MigrationBackupPath { get; private set; }
    public int SchemaVersion => 7;
    public event EventHandler? SitesChanged;

    public Database(string path)
    {
        _databasePath = Path.GetFullPath(path);
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
        using (var version = connection.CreateCommand())
        {
            version.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='SchemaVersion'";
            if (Convert.ToInt32(version.ExecuteScalar()) != 0)
            {
                version.CommandText = "SELECT Version FROM SchemaVersion LIMIT 1";
                var current = Convert.ToInt32(version.ExecuteScalar());
                if (current > SchemaVersion) throw new InvalidDataException("このDBは新しいバージョンで作成されています。");
                if (current == SchemaVersion) return;
                CreateMigrationBackup(connection);
            }
        }
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
                  LastError TEXT, EffectiveMode TEXT, MonitorRevision INTEGER NOT NULL DEFAULT 0, UseBrowserCompatibleUserAgent INTEGER NOT NULL DEFAULT 0,
                  UpdateDialogNotification INTEGER NOT NULL DEFAULT 0, NotificationTargetUrl TEXT);
                CREATE TABLE IF NOT EXISTS History (
                  Id INTEGER PRIMARY KEY AUTOINCREMENT, SiteId INTEGER NOT NULL, ChangedAt TEXT NOT NULL,
                  OldHash TEXT, NewHash TEXT NOT NULL, OldPreview TEXT, NewPreview TEXT NOT NULL, NotificationTargetUrl TEXT,
                  FOREIGN KEY(SiteId) REFERENCES Sites(Id) ON DELETE CASCADE);
                CREATE INDEX IF NOT EXISTS IX_Sites_Enabled_NextDue ON Sites(Enabled, NextDue);
                CREATE INDEX IF NOT EXISTS IX_History_SiteId_ChangedAt ON History(SiteId, ChangedAt DESC);
                CREATE TABLE IF NOT EXISTS PendingUpdateDialogs (
                  Id INTEGER PRIMARY KEY AUTOINCREMENT, HistoryId INTEGER NOT NULL UNIQUE,
                  SiteId INTEGER NOT NULL, CreatedAt TEXT NOT NULL, Url TEXT NOT NULL);
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
        if (!columns.Contains("UpdateDialogNotification"))
            Execute(connection, transaction, "ALTER TABLE Sites ADD COLUMN UpdateDialogNotification INTEGER NOT NULL DEFAULT 0;");
        if (!columns.Contains("NotificationTargetUrl"))
            Execute(connection, transaction, "ALTER TABLE Sites ADD COLUMN NotificationTargetUrl TEXT;");
        using (var historyColumns = connection.CreateCommand())
        {
            historyColumns.Transaction = transaction;
            historyColumns.CommandText = "PRAGMA table_info(History)";
            using var reader = historyColumns.ExecuteReader();
            var hasTarget = false;
            while (reader.Read()) hasTarget |= reader.GetString(1) == "NotificationTargetUrl";
            reader.Close();
            if (!hasTarget) Execute(connection, transaction, "ALTER TABLE History ADD COLUMN NotificationTargetUrl TEXT;");
        }
        Execute(connection, transaction, "UPDATE SchemaVersion SET Version=7;");
        transaction.Commit();
    }

    private void CreateMigrationBackup(SqliteConnection source)
    {
        var backup = _databasePath + ".pre-v1.0.7-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N") + ".bak";
        var temporary = backup + ".tmp";
        try
        {
            using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = temporary, Pooling = false }.ToString()))
            {
                destination.Open();
                source.BackupDatabase(destination);
            }
            File.Move(temporary, backup);
            MigrationBackupPath = backup;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public PendingUpdateDialog? GetNextPendingUpdateDialog()
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT p.Id,p.SiteId,p.CreatedAt,p.Url,s.NotificationTargetUrl FROM PendingUpdateDialogs p LEFT JOIN Sites s ON s.Id=p.SiteId ORDER BY p.Id LIMIT 1";
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? new PendingUpdateDialog(reader.GetInt64(0), reader.GetInt64(1), DateTimeOffset.Parse(reader.GetString(2)), NotificationUrl.ForStored(reader.GetString(3), NullableString(reader, 4))) : null;
    }

    public bool AcknowledgeUpdateDialog(long id)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM PendingUpdateDialogs WHERE Id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() == 1;
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
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var id = SaveSite(connection, transaction, site);
        transaction.Commit();
        site.Id = id;
        OnSitesChanged();
        return id;
    }

    private static long SaveSite(SqliteConnection connection, SqliteTransaction transaction, Site site)
    {
        SiteValidation.Validate(site);
        if (site.MonitorMode != MonitorMode.Feed) site.FeedUrl = null;
        var now = DateTimeOffset.Now;
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
            INSERT INTO Sites(Name,Url,NotificationTargetUrl,Enabled,MonitorMode,FeedUrl,AutoDetectedFeedUrl,Selector,XPath,Regex,ScheduleMode,IntervalMinutes,DailyTime,WindowsNotification,PopupNotification,SoundNotification,SoundFile,SoundVolume,LastHash,LastPreview,LastETag,LastModified,LastChecked,LastChanged,LastNotifiedHash,NextDue,ConsecutiveErrors,LastError,EffectiveMode,MonitorRevision,UseBrowserCompatibleUserAgent,UpdateDialogNotification)
            VALUES($Name,$Url,$NotificationTargetUrl,$Enabled,$MonitorMode,$FeedUrl,$AutoDetectedFeedUrl,$Selector,$XPath,$Regex,$ScheduleMode,$IntervalMinutes,$DailyTime,$WindowsNotification,$PopupNotification,$SoundNotification,$SoundFile,$SoundVolume,$LastHash,$LastPreview,$LastETag,$LastModified,$LastChecked,$LastChanged,$LastNotifiedHash,$NextDue,$ConsecutiveErrors,$LastError,$EffectiveMode,$MonitorRevision,$UseBrowserCompatibleUserAgent,$UpdateDialogNotification);
            SELECT last_insert_rowid();
            """ : """
            UPDATE Sites SET Name=$Name,Url=$Url,NotificationTargetUrl=$NotificationTargetUrl,Enabled=$Enabled,MonitorMode=$MonitorMode,FeedUrl=$FeedUrl,AutoDetectedFeedUrl=$AutoDetectedFeedUrl,Selector=$Selector,XPath=$XPath,Regex=$Regex,ScheduleMode=$ScheduleMode,IntervalMinutes=$IntervalMinutes,DailyTime=$DailyTime,WindowsNotification=$WindowsNotification,PopupNotification=$PopupNotification,SoundNotification=$SoundNotification,SoundFile=$SoundFile,SoundVolume=$SoundVolume,LastHash=$LastHash,LastPreview=$LastPreview,LastETag=$LastETag,LastModified=$LastModified,LastChecked=$LastChecked,LastChanged=$LastChanged,LastNotifiedHash=$LastNotifiedHash,NextDue=$NextDue,ConsecutiveErrors=$ConsecutiveErrors,LastError=$LastError,EffectiveMode=$EffectiveMode,MonitorRevision=$MonitorRevision,UseBrowserCompatibleUserAgent=$UseBrowserCompatibleUserAgent,UpdateDialogNotification=$UpdateDialogNotification WHERE Id=$Id;
            SELECT $Id;
            """;
        AddSiteParameters(command, site);
        var id = Convert.ToInt64(command.ExecuteScalar());
        return id;
    }

    public void ImportUserSettings(IReadOnlyList<SiteSettings> definitions, ConfigurationImportMode mode, Action saveAppSettings)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var existing = new List<Site>();
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT * FROM Sites ORDER BY Id";
            using var reader = read.ExecuteReader();
            while (reader.Read()) existing.Add(ReadSite(reader));
        }
        var keep = new HashSet<long>();
        foreach (var definition in definitions)
        {
            var site = existing.FirstOrDefault(definition.Matches);
            var isNew = site is null;
            site = definition.ApplyTo(site);
            site.Id = SaveSite(connection, transaction, site);
            keep.Add(site.Id);
            if (isNew) existing.Add(site);
        }
        if (mode == ConfigurationImportMode.Replace)
        {
            foreach (var site in existing.Where(s => !keep.Contains(s.Id)))
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "SELECT EXISTS(SELECT 1 FROM History WHERE SiteId=$id) OR EXISTS(SELECT 1 FROM PendingUpdateDialogs WHERE SiteId=$id)";
                command.Parameters.AddWithValue("$id", site.Id);
                if (Convert.ToInt64(command.ExecuteScalar()) != 0)
                    throw new ConfigurationException("置換で削除されるサイトに履歴または未確認通知があります。既存設定は変更していません。「追加・統合」を選択してください。");
                command.CommandText = "DELETE FROM Sites WHERE Id=$id";
                command.ExecuteNonQuery();
            }
        }
        saveAppSettings();
        transaction.Commit();
        // A post-commit observer is not part of the storage transaction and must not trigger
        // a settings rollback after the DB has already committed.
        try { OnSitesChanged(); } catch { }
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

    public CheckResult ApplySuccess(long siteId, long expectedRevision, string hash, string preview, string? etag, string? lastModified, MonitorMode effectiveMode, DateTimeOffset now, bool updateAutoDetectedFeed = false, string? autoDetectedFeedUrl = null, bool dialogNotificationsEnabled = true, string? notificationUrl = null)
    {
        using var c = Open(); using var tx = c.BeginTransaction();
        var site = GetSiteForUpdate(c, tx, siteId) ?? throw new InvalidOperationException("監視サイトが見つかりません。");
        if (site.MonitorRevision != expectedRevision)
        {
            tx.Rollback();
            return Discarded(site);
        }

        var outcome = UpdateDecision.Decide(site.LastHash, hash);
        var notify = UpdateDecision.ShouldNotify(site.LastNotifiedHash, hash, outcome);
        var eventUrl = string.IsNullOrWhiteSpace(site.NotificationTargetUrl)
            ? NotificationUrl.Sanitize(notificationUrl ?? NotificationUrl.ForSite(site)) : NotificationUrl.ForSite(site);
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
            history.CommandText = "INSERT INTO History(SiteId,ChangedAt,OldHash,NewHash,OldPreview,NewPreview,NotificationTargetUrl) VALUES($id,$at,$old,$new,$op,$np,$url)";
            history.Parameters.AddWithValue("$id", siteId);
            history.Parameters.AddWithValue("$at", now.ToString("O"));
            history.Parameters.AddWithValue("$old", Db(site.LastHash));
            history.Parameters.AddWithValue("$new", hash);
            history.Parameters.AddWithValue("$op", site.LastPreview is null ? DBNull.Value : NotificationUrl.RedactText(site.LastPreview));
            history.Parameters.AddWithValue("$np", NotificationUrl.RedactText(preview));
            history.Parameters.AddWithValue("$url", eventUrl);
            history.ExecuteNonQuery();
            if (notify && site.UpdateDialogNotification && dialogNotificationsEnabled)
            {
                Execute(c, tx, """
                    INSERT INTO PendingUpdateDialogs(HistoryId,SiteId,CreatedAt,Url)
                    VALUES(last_insert_rowid(),$id,$at,$url);
                    """, ("$id", siteId), ("$at", now.ToString("O")),
                    ("$url", eventUrl));
            }
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
        return new CheckResult(outcome, site, outcome switch
        {
            CheckOutcome.BaselineCreated => "初回基準を保存しました",
            CheckOutcome.Changed => "更新を検出しました",
            _ => "更新なし"
        }, hash, NotificationUrl.RedactText(preview), notify, expectedRevision, eventUrl);
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
        => ApplyErrorWithCommittedCount(siteId, expectedRevision, error, now).HasValue;

    public int? ApplyErrorWithCommittedCount(long siteId, long expectedRevision, string error, DateTimeOffset now)
    {
        using var c = Open(); using var tx = c.BeginTransaction();
        var site = GetSiteForUpdate(c, tx, siteId) ?? throw new InvalidOperationException("監視サイトが見つかりません。");
        if (site.MonitorRevision != expectedRevision) { tx.Rollback(); return null; }
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE Sites SET LastChecked=$at,NextDue=$next,ConsecutiveErrors=ConsecutiveErrors+1,LastError=$error WHERE Id=$id AND MonitorRevision=$revision";
        cmd.Parameters.AddWithValue("$at", now.ToString("O"));
        cmd.Parameters.AddWithValue("$next", Db(ScheduleCalculator.NextDue(WithLastChecked(site, now), now)?.ToString("O")));
        cmd.Parameters.AddWithValue("$error", ContentHasher.Preview(NotificationUrl.RedactText(error), 2000));
        cmd.Parameters.AddWithValue("$id", siteId);
        cmd.Parameters.AddWithValue("$revision", expectedRevision);
        if (cmd.ExecuteNonQuery() != 1) { tx.Rollback(); return null; }
        // Read our own increment under the same transaction's write lock.
        using var count = c.CreateCommand();
        count.Transaction = tx;
        count.CommandText = "SELECT ConsecutiveErrors FROM Sites WHERE Id=$id";
        count.Parameters.AddWithValue("$id", siteId);
        var committedCount = checked((int)(long)count.ExecuteScalar()!);
        tx.Commit();
        return committedCount;
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
        cmd.CommandText = "SELECT h.Id,h.SiteId,s.Name,h.ChangedAt,h.OldHash,h.NewHash,h.OldPreview,h.NewPreview,COALESCE(h.NotificationTargetUrl,s.NotificationTargetUrl,s.Url) FROM History h JOIN Sites s ON s.Id=h.SiteId WHERE ($site IS NULL OR h.SiteId=$site) ORDER BY h.ChangedAt DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$site", siteId is null ? DBNull.Value : siteId.Value);
        cmd.Parameters.AddWithValue("$limit", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<HistoryEntry>();
        while (r.Read()) list.Add(new(r.GetInt64(0), r.GetInt64(1), NotificationUrl.RedactText(r.GetString(2)), DateTimeOffset.Parse(r.GetString(3)), NullableString(r, 4), r.GetString(5), r.IsDBNull(6) ? null : NotificationUrl.RedactText(r.GetString(6)), NotificationUrl.RedactText(r.GetString(7)), NotificationUrl.Sanitize(r.GetString(8))));
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
        NotificationTargetUrl = NullableString(r, r.GetOrdinal("NotificationTargetUrl")),
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
        UpdateDialogNotification = r.GetBoolean(r.GetOrdinal("UpdateDialogNotification")),
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
        LastError = r.IsDBNull(r.GetOrdinal("LastError")) ? null : NotificationUrl.RedactText(r.GetString(r.GetOrdinal("LastError"))),
        EffectiveMode = NullableString(r, r.GetOrdinal("EffectiveMode") )
    };

    private static void AddSiteParameters(SqliteCommand cmd, Site s)
    {
        var values = new Dictionary<string, object?>
        {
            ["Id"] = s.Id, ["Name"] = s.Name, ["Url"] = s.Url, ["NotificationTargetUrl"] = s.NotificationTargetUrl, ["Enabled"] = s.Enabled,
            ["MonitorMode"] = (int)s.MonitorMode, ["FeedUrl"] = s.FeedUrl, ["AutoDetectedFeedUrl"] = s.AutoDetectedFeedUrl,
            ["Selector"] = s.Selector, ["XPath"] = s.XPath, ["Regex"] = s.Regex, ["ScheduleMode"] = (int)s.ScheduleMode,
            ["IntervalMinutes"] = s.IntervalMinutes, ["DailyTime"] = s.DailyTime, ["WindowsNotification"] = s.WindowsNotification,
            ["PopupNotification"] = s.PopupNotification, ["SoundNotification"] = s.SoundNotification, ["SoundFile"] = s.SoundFile,
            ["UpdateDialogNotification"] = s.UpdateDialogNotification,
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
