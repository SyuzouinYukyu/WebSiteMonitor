using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;
using Microsoft.Data.Sqlite;
using WebSiteMonitor.Core;

namespace WebSiteMonitor.Tests;

[Collection("V106 Windows Forms")]
public sealed class V111Tests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WebSiteMonitorV111-" + Guid.NewGuid().ToString("N"));
    private static readonly Uri Target = new("https://example.test/");
    private const string Password = "v111-password-secret";
    public V111Tests() => Directory.CreateDirectory(_root);
    private Database Db() { var db = new Database(Path.Combine(_root, "test.db")); db.Initialize(); return db; }
    private static Site Site() => new() { Name = "監視テスト", Url = Target.ToString(), MonitorMode = MonitorMode.Text, ScheduleMode = ScheduleMode.Manual };
    private MonitorEngine Engine(Database db, IHttpFetcher fetcher, int testMs = 45000, int monitorMs = 100000)
        => new(db, fetcher, new FileLogger(Path.Combine(_root, "logs")), testTimeout: TimeSpan.FromMilliseconds(testMs), monitorTimeout: TimeSpan.FromMilliseconds(monitorMs));
    private global::WebSiteMonitor.ExportPasswordStore Store() => new(Path.Combine(_root, "local", "export-password.dpapi"));

    [Fact]
    public void PasswordPromptRememberReloadChangeForgetAndImportRemainSeparate()
    {
        var store = Store(); var prompts = 0;
        string? Prompt() { prompts++; return Password; }
        Assert.Equal(Password, store.ResolvePassword(false, Prompt)); Assert.Equal(1, prompts);
        Assert.Null(store.TryLoad()); // Only a successful export remembers it.
        var doc = ConfigurationTransfer.Capture([Site()], new AppSettings());
        var backup = Path.Combine(_root, "old.wsmcfg");
        Assert.True(store.ExportAndRemember(backup, doc, Password));
        Assert.Equal(Password, Store().ResolvePassword(false, () => throw new Exception("unexpected prompt")));
        Assert.Equal(Password, store.ResolvePassword(true, Prompt)); Assert.Equal(2, prompts);
        Assert.Equal(Password, Store().TryLoad());
        var db = Db(); var settings = new SettingsStore(Path.Combine(_root, "settings.json"));
        ConfigurationTransfer.Import(ConfigurationTransfer.Read(backup, Password), ConfigurationImportMode.Merge, db, settings);
        Assert.Equal(Password, Store().TryLoad());
        Assert.DoesNotContain(Password, File.ReadAllText(Path.Combine(_root, "settings.json")));
        SqliteConnection.ClearAllPools();
        Assert.DoesNotContain(Password, Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(_root, "test.db"))));
        store.Save("changed-password"); Assert.Equal("changed-password", Store().TryLoad());
        Assert.Equal(1, ConfigurationTransfer.Read(backup, Password).FormatVersion);
        Assert.Throws<ConfigurationException>(() => ConfigurationTransfer.Read(backup, "changed-password"));
        store.Forget(); Assert.Null(Store().TryLoad());
        Assert.Equal(Password, Store().ResolvePassword(false, Prompt)); Assert.Equal(3, prompts);
        var bytes = File.ReadAllBytes(backup);
        Assert.DoesNotContain(Password, Encoding.UTF8.GetString(bytes));
        Assert.DoesNotContain("ExportPassword", System.Text.Json.JsonSerializer.Serialize(doc));
    }

    [Fact]
    public void PasswordCorruptionForeignUnprotectAndInvalidInputFallBackWithoutChangingOldState()
    {
        var store = Store(); store.Save(Password);
        var path = Path.Combine(_root, "local", "export-password.dpapi"); var blob = File.ReadAllBytes(path);
        Assert.DoesNotContain(Password, Encoding.UTF8.GetString(blob));
        Assert.DoesNotContain(Password, Encoding.Unicode.GetString(blob));
        Assert.Throws<ArgumentException>(() => store.Save("short"));
        Assert.Throws<ArgumentException>(() => store.Save("        "));
        var failing = new global::WebSiteMonitor.ExportPasswordStore(path, write: (_, _) => throw new IOException("do not expose details"));
        Assert.Throws<IOException>(() => failing.Save("new-password")); Assert.Equal(blob, File.ReadAllBytes(path));
        var foreign = new global::WebSiteMonitor.ExportPasswordStore(path, unprotect: _ => throw new CryptographicException("foreign user"));
        Assert.Equal("fresh-password", foreign.ResolvePassword(false, () => "fresh-password"));
        File.WriteAllBytes(path, [1, 2, 3]); Assert.Null(Store().TryLoad());
        Assert.Null(Store().ResolvePassword(false, () => null));
    }

    [Fact]
    public void SuccessfulBackupSurvivesDpapiSaveFailureAndFailedExportDoesNotRemember()
    {
        var doc = ConfigurationTransfer.Capture([Site()], new AppSettings());
        var store = new global::WebSiteMonitor.ExportPasswordStore(Path.Combine(_root, "local", "pw"), protect: _ => throw new CryptographicException());
        var path = Path.Combine(_root, "backup.wsmcfg");
        Assert.False(store.ExportAndRemember(path, doc, Password));
        Assert.Single(ConfigurationTransfer.Read(path, Password).Sites); Assert.Null(store.TryLoad());
        var good = Store();
        Assert.Throws<ConfigurationException>(() => good.ExportAndRemember(_root, doc, Password)); Assert.Null(good.TryLoad());
    }

    [Fact]
    public void ActualV110ReleaseEncryptsAndDecryptsCompatibleFormatOne()
    {
        var reference = ConfigurationTransfer.Decrypt(Convert.FromBase64String(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "v110-settings.wsmcfg.base64"))), Password);
        Assert.Equal("legacy", reference.Sites.Single().Name); Assert.Equal(1, reference.FormatVersion);
        // Use the immutable released Core, not a fixture generated by the new implementation.
        var source = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../..", "v1.1.0/release/WebSiteMonitor_v1.1.0.exe"));
        // Published source-only checkouts retain the actual v1.1.0 encrypted reference above.
        if (!File.Exists(source))
        {
            var referenceDb = Db();
            ConfigurationTransfer.Import(reference, ConfigurationImportMode.Merge, referenceDb, new SettingsStore(Path.Combine(_root, "settings.json")));
            Assert.Equal("legacy", referenceDb.GetSites().Single().Name); Assert.Equal(7, referenceDb.GetSchemaVersion());
            return;
        }
        var bytes = File.ReadAllBytes(source);
        var signature = Convert.FromHexString("8B1202B96A612038727B930214D7A03213F5B9E6EFAE3318EE3B2DCE24B36AAE");
        var marker = bytes.AsSpan().IndexOf(signature); Assert.True(marker >= 8);
        using var reader = new BinaryReader(new MemoryStream(bytes));
        reader.BaseStream.Position = BitConverter.ToInt64(bytes, marker - 8);
        var major = reader.ReadUInt32(); reader.ReadUInt32(); var count = reader.ReadInt32(); reader.ReadString();
        Assert.Equal(6U, major); reader.BaseStream.Position += 40;
        byte[]? core = null;
        for (var i = 0; i < count; i++)
        {
            var offset = reader.ReadInt64(); var size = reader.ReadInt64(); var compressed = reader.ReadInt64(); reader.ReadByte(); var name = reader.ReadString();
            if (name != "WebSiteMonitor.Core.dll") continue;
            Assert.Equal(0L, compressed); core = bytes.AsSpan((int)offset, (int)size).ToArray();
        }
        Assert.NotNull(core);
        var context = new AssemblyLoadContext("immutable-v110", true);
        try
        {
            using var stream = new MemoryStream(core); var assembly = context.LoadFromStream(stream);
            var siteType = assembly.GetType("WebSiteMonitor.Core.Site")!; var legacySite = Activator.CreateInstance(siteType)!;
            siteType.GetProperty("Name")!.SetValue(legacySite, "legacy"); siteType.GetProperty("Url")!.SetValue(legacySite, Target.ToString());
            var sites = Array.CreateInstance(siteType, 1); sites.SetValue(legacySite, 0);
            var transfer = assembly.GetType("WebSiteMonitor.Core.ConfigurationTransfer")!;
            var settings = Activator.CreateInstance(assembly.GetType("WebSiteMonitor.Core.AppSettings")!)!;
            var document = transfer.GetMethod("Capture")!.Invoke(null, [sites, settings]);
            var legacy = (byte[])transfer.GetMethod("Encrypt")!.Invoke(null, [document, Password])!;
            var restored = ConfigurationTransfer.Decrypt(legacy, Password); Assert.Equal("legacy", restored.Sites.Single().Name);
            Assert.Equal(1, BitConverter.ToInt32(legacy, 8)); Assert.Equal(200000, BitConverter.ToInt32(legacy, 12));
            var current = ConfigurationTransfer.Encrypt(restored, Password);
            Assert.NotNull(transfer.GetMethod("Decrypt")!.Invoke(null, [current, Password]));
            var db = Db(); ConfigurationTransfer.Import(restored, ConfigurationImportMode.Merge, db, new SettingsStore(Path.Combine(_root, "settings.json")));
            Assert.Equal("legacy", db.GetSites().Single().Name); Assert.Equal(7, db.GetSchemaVersion());
        }
        finally { context.Unload(); }
    }

    [Theory]
    [InlineData(200)]
    [InlineData(304)]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(503)]
    public async Task StatusConditionalHeadersRetryCountAndRetryAfterAreBounded(int status)
    {
        var count = 0;
        using var client = new HttpClient(new Handler((request, _) =>
        {
            count++; Assert.Equal("\"etag\"", request.Headers.IfNoneMatch.Single().ToString()); Assert.NotNull(request.Headers.IfModifiedSince);
            var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("<p>OK</p>") };
            if (status == 429) response.Headers.RetryAfter = new(TimeSpan.FromHours(2));
            return Task.FromResult(response);
        }));
        using var fetcher = new SharedHttpFetcher(client, operationTimeout: TimeSpan.FromSeconds(5));
        var operation = fetcher.FetchAsync(Target, "\"etag\"", "Tue, 01 Sep 2026 00:00:00 GMT", default);
        if (status is 200 or 304) Assert.Equal(status, (await operation).StatusCode);
        else if (status == 429) await Assert.ThrowsAsync<TimeoutException>(() => operation);
        else await Assert.ThrowsAsync<HttpRequestException>(() => operation);
        Assert.Equal(status == 503 ? 3 : 1, count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task RealLoopbackBodyStallTimesOutClosesConnectionAndLeavesNoNetworkTask(int stage)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var server = Task.Run(async () =>
        {
            using var connection = await listener.AcceptTcpClientAsync(deadline.Token);
            await using var stream = connection.GetStream();
            var buffer = new byte[4096]; Assert.True(await stream.ReadAsync(buffer, deadline.Token) > 0);
            if (stage != 0)
            {
                var headers = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 1000\r\nConnection: close\r\n\r\n" + (stage == 2 ? "partial" : ""));
                await stream.WriteAsync(headers, deadline.Token);
            }
            try { Assert.Equal(0, await stream.ReadAsync(buffer, deadline.Token)); } catch (IOException) { /* TCP reset is also closed. */ }
        });
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false });
        using var fetcher = new SharedHttpFetcher(client, attemptTimeout: TimeSpan.FromMilliseconds(150), operationTimeout: TimeSpan.FromMilliseconds(250));
        var watch = Stopwatch.StartNew();
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() => fetcher.FetchAsync(new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/"), null, null, default));
            await server; Assert.True(server.IsCompletedSuccessfully); Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task HeaderStallAndRetryWaitReceiveRealCancellation()
    {
        var active = 0; var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(new Handler(async (_, token) =>
        {
            active++; started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); return new HttpResponseMessage(HttpStatusCode.OK); }
            finally { active--; }
        }));
        using var fetcher = new SharedHttpFetcher(client);
        using var cancel = new CancellationTokenSource();
        var work = fetcher.FetchAsync(Target, null, null, cancel.Token); await started.Task; cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work); Assert.Equal(0, active); Assert.True(work.IsCompleted);
        var attempts = 0;
        using var retryClient = new HttpClient(new Handler((_, _) =>
        { attempts++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Headers = { RetryAfter = new(TimeSpan.FromSeconds(3)) } }); }));
        using var retry = new SharedHttpFetcher(retryClient); using var waitCancel = new CancellationTokenSource(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => retry.FetchAsync(Target, null, null, waitCancel.Token)); Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task RetryAfterUsesManualOperationRemainingBudgetInsteadOfFetcherDefault()
    {
        var requests = 0;
        using var client = new HttpClient(new Handler((_, _) =>
        { requests++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Headers = { RetryAfter = new(TimeSpan.FromSeconds(3)) } }); }));
        using var fetcher = new SharedHttpFetcher(client);
        var engine = Engine(Db(), fetcher, 2000);
        var watch = Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(() => engine.TestExtractAsync(Site(), default));
        Assert.Equal(1, requests); Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task SizeLimitIsAppliedToUnknownLengthBody()
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(new NonSeekableBody(new byte[1025])) })));
        using var fetcher = new SharedHttpFetcher(client, 1024);
        await Assert.ThrowsAsync<InvalidDataException>(() => fetcher.FetchAsync(Target, null, null, default));
    }

    [Fact]
    public async Task ProductionClientRedirectsAndDecompressesGzip()
    {
        using var plain = new MemoryStream(); using (var gzip = new GZipStream(plain, CompressionMode.Compress, true)) gzip.Write(Encoding.UTF8.GetBytes("<p>compressed OK</p>"));
        var compressed = plain.ToArray(); var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            for (var i = 0; i < 2; i++)
            {
                using var connection = await listener.AcceptTcpClientAsync(cancel.Token); await using var stream = connection.GetStream();
                var buffer = new byte[4096]; var n = await stream.ReadAsync(buffer, cancel.Token);
                Assert.Contains("WebSiteMonitor/1.1.4", Encoding.ASCII.GetString(buffer, 0, n));
                var header = i == 0 ? $"HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:{port}/body\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
                    : $"HTTP/1.1 200 OK\r\nContent-Encoding: gzip\r\nContent-Length: {compressed.Length}\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(header), cancel.Token); if (i == 1) await stream.WriteAsync(compressed, cancel.Token);
            }
        });
        using var fetcher = new SharedHttpFetcher();
        try { var result = await fetcher.FetchAsync(new Uri($"http://127.0.0.1:{port}/"), null, null, cancel.Token); Assert.Equal("<p>compressed OK</p>", SharedHttpFetcher.DecodeBody(result)); Assert.EndsWith("/body", result.FinalUri.ToString()); await server; }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task EngineDeadlinesPreserveHashHistoryPendingAndDoNotNotify()
    {
        var db = Db(); var site = Site(); db.SaveSite(site);
        db.ApplySuccess(site.Id, "A", "A", null, null, MonitorMode.Text, DateTimeOffset.Now); db.ApplySuccess(site.Id, "B", "B", null, null, MonitorMode.Text, DateTimeOffset.Now);
        var pending = db.GetNextPendingUpdateDialog(); var old = db.GetSite(site.Id)!;
        var fetcher = new BlockingFetcher(); var engine = Engine(db, fetcher, 100, 100);
        await Assert.ThrowsAsync<TimeoutException>(() => engine.TestExtractAsync(site, default)); Assert.Equal(0, fetcher.Active);
        var result = await engine.CheckAsync(site, default); Assert.Equal(CheckOutcome.Failed, result.Outcome); Assert.False(result.ShouldNotify);
        Assert.Equal(old.LastHash, db.GetSite(site.Id)!.LastHash); Assert.Equal(old.LastChanged, db.GetSite(site.Id)!.LastChanged);
        Assert.Single(db.GetHistory()); Assert.Equal(pending, db.GetNextPendingUpdateDialog()); Assert.Equal(0, fetcher.Active);
        var cancel = new CancellationTokenSource(50); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.CheckAsync(site, cancel.Token));
        Assert.Equal(0, fetcher.Active); Assert.Single(db.GetHistory());
    }

    [Fact]
    public async Task FixedCtronicsHtmlExtractsVersionWithoutChangingFullPageHashAndSafelyPreviewsQuotes()
    {
        var html = "<html><body><a href=\"https://example.test/?token=attribute-secret\">HiP2P(For windows)_v6.5.9.3</a><script>" + new string('x', 150000) + "</script><p>Bearer bearer-secret token=text-secret</p></body></html>";
        var site = Site(); site.MonitorMode = MonitorMode.Regex; site.Regex = @"HiP2P\(For windows\)_v([0-9.]+)";
        var db = Db(); var engine = Engine(db, new FixedFetcher(html));
        Assert.Equal("6.5.9.3", (await engine.TestExtractAsync(site, default)).Content);
        var preview = ContentHasher.SafePreview(html, MonitorMode.FullPage, 12000);
        Assert.Contains("HiP2P(For windows)_v6.5.9.3", preview); Assert.DoesNotContain("attribute-secret", preview); Assert.DoesNotContain("text-secret", preview); Assert.DoesNotContain("bearer-secret", preview);
        Assert.Equal(ContentHasher.Sha256(ContentExtractor.NormalizePage(html)), ContentHasher.Sha256(ContentExtractor.Extract(html, MonitorMode.FullPage).Content));
        var quoted = "<p title=\"" + new string('a', 13000) + "\">visible content</p>";
        Assert.Equal("[DIAGNOSTIC_REDACTED]", NotificationUrl.RedactText(ContentHasher.Preview(quoted, 12000)));
        Assert.Equal("visible content", ContentHasher.SafePreview(quoted, MonitorMode.FullPage, 12000));
        Assert.Equal("[DIAGNOSTIC_REDACTED]", ContentHasher.SafePreview("password=\"broken secret", MonitorMode.Text));
        Assert.DoesNotContain("encoded-secret", ContentHasher.SafePreview("%2574oken=encoded-secret", MonitorMode.Text));
        Assert.DoesNotContain("url-secret", ContentHasher.SafePreview("<b>prefix</b>https://example.test/url-secret", MonitorMode.FullPage));
    }

    [Fact]
    public async Task SchedulerDrainCancelsActiveChecksBlocksNewChecksAndRetainsDatabase()
    {
        var db = Db(); var site = Site(); db.SaveSite(site); var fetcher = new BlockingFetcher();
        using var scheduler = new global::WebSiteMonitor.OneShotScheduler(db, Engine(db, fetcher));
        var running = scheduler.CheckNowAsync([site]); await fetcher.Started.Task;
        await scheduler.StopAndDrainAsync(TimeSpan.FromSeconds(2)); await running;
        Assert.Equal(0, fetcher.Active); Assert.Equal(0, scheduler.RunningCount); Assert.True(running.IsCompleted);
        await scheduler.CheckNowAsync([site]); Assert.Equal(1, fetcher.Count); Assert.Equal(7, db.GetSchemaVersion()); Assert.Single(db.GetSites());
    }

    [Fact]
    public async Task AudioDrainRefusesSafeShutdownWhileWorkerStillRuns()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sound = new global::WebSiteMonitor.SoundService(async (_, _) =>
        { started.TrySetResult(); await release.Task; return new(global::WebSiteMonitor.SoundPlaybackState.Completed, "完了"); });
        var input = Path.Combine(_root, "probe.wav"); File.WriteAllBytes(input, [0]);
        var work = sound.PlayAsync(input, 100, new AppSettings()); await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAsync<TimeoutException>(() => sound.ShutdownAndDrainAsync(TimeSpan.FromMilliseconds(50)));
        release.TrySetResult(); await work; await sound.ShutdownAndDrainAsync(); Assert.True(work.IsCompleted);
    }

    [Fact]
    public void EditorCancelDoubleClickCloseAndUiRecoveryWaitForActualCompletion()
    {
        RunSta(() =>
        {
            var db = Db(); var fetcher = new BlockingFetcher(); using var sound = new global::WebSiteMonitor.SoundService();
            using var form = new global::WebSiteMonitor.SiteEditForm(Site(), Engine(db, fetcher), sound, new AppSettings(), _root);
            form.Show(); Application.DoEvents(); var running = form.RunMonitorTestAsync(); PumpUntil(() => fetcher.Active == 1);
            Assert.Equal("中止", Get<Button>(form, "_test").Text); Assert.Same(running, form.RunMonitorTestAsync());
            PumpUntil(() => running.IsCompleted); running.GetAwaiter().GetResult();
            Assert.Equal(1, fetcher.Count); Assert.Equal(0, fetcher.Active); Assert.True(Get<Button>(form, "_test").Enabled);
            Assert.Contains("中止しました", Get<TextBox>(form, "_preview").Text); Assert.DoesNotContain("取得中", Get<TextBox>(form, "_preview").Text);
            var closing = form.RunMonitorTestAsync(); PumpUntil(() => fetcher.Active == 1); form.Close(); form.Dispose();
            PumpUntil(() => closing.IsCompleted); closing.GetAwaiter().GetResult(); Assert.Equal(0, fetcher.Active);
            global::WebSiteMonitor.SiteEditForm.CancelAndDrainTestsAsync().GetAwaiter().GetResult();
        });
    }

    [Fact]
    public void EditorTimeoutShowsDistinctStateAndProtectedEditorsBlockRestart()
    {
        RunSta(() =>
        {
            var db = Db(); using var sound = new global::WebSiteMonitor.SoundService(); var fetcher = new BlockingFetcher();
            using var form = new global::WebSiteMonitor.SiteEditForm(Site(), Engine(db, fetcher, 100), sound, new AppSettings(), _root);
            form.Show(); Application.DoEvents(); Assert.True(global::WebSiteMonitor.TrayApplicationContext.HasProtectedEditors());
            var work = form.RunMonitorTestAsync(); PumpUntil(() => work.IsCompleted); work.GetAwaiter().GetResult();
            Assert.Contains("タイムアウト", Get<TextBox>(form, "_preview").Text); Assert.True(Get<Button>(form, "_test").Enabled); Assert.Equal(0, fetcher.Active);
            form.Close(); Assert.False(global::WebSiteMonitor.TrayApplicationContext.HasProtectedEditors());
        });
    }

    [Fact]
    public void ConfigurationInProgressBlocksRestartBeforeConfirmationOrHelperLaunch()
    {
        RunSta(() =>
        {
            var confirmations = 0; var launches = 0;
            using var single = new global::WebSiteMonitor.SingleInstanceCoordinator(Guid.NewGuid().ToString("N"));
            using var context = new global::WebSiteMonitor.TrayApplicationContext(global::WebSiteMonitor.AppPaths.CreateAndVerify(_root), single, true, true,
                restartStarter: () => { launches++; throw new InvalidOperationException(); }, restartConfirmation: () => { confirmations++; return false; });
            typeof(global::WebSiteMonitor.TrayApplicationContext).GetField("_configurationBusy", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(context, true);
            var work = (Task)typeof(global::WebSiteMonitor.TrayApplicationContext).GetMethod("RestartAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(context, null)!;
            Assert.True(work.IsCompletedSuccessfully); Assert.Equal(0, confirmations); Assert.Equal(0, launches);
        });
    }

    [Fact]
    public async Task RestartWaitRejectsPidReuseForeignProcessAndTimeoutWithoutForceLaunching()
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell/v1.0/powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        info.ArgumentList.Add("-NoProfile"); info.ArgumentList.Add("-Command"); info.ArgumentList.Add("Start-Sleep -Seconds 30");
        using var child = Process.Start(info)!;
        try
        {
            var ticks = child.StartTime.ToUniversalTime().Ticks; var exe = child.MainModule!.FileName;
            Assert.False(await global::WebSiteMonitor.RestartLauncher.WaitForOldProcessAsync(child.Id, ticks + 1, exe, TimeSpan.FromMilliseconds(50)));
            Assert.False(await global::WebSiteMonitor.RestartLauncher.WaitForOldProcessAsync(child.Id, ticks, "C:/not-the-process.exe", TimeSpan.FromMilliseconds(50)));
            Assert.False(await global::WebSiteMonitor.RestartLauncher.WaitForOldProcessAsync(child.Id, ticks, exe, TimeSpan.FromMilliseconds(100)));
            Assert.False(child.HasExited);
            var wait = global::WebSiteMonitor.RestartLauncher.WaitForOldProcessAsync(child.Id, ticks, exe, TimeSpan.FromSeconds(5));
            Assert.False(wait.IsCompleted); child.Kill(); await child.WaitForExitAsync(); Assert.True(await wait);
        }
        finally { if (!child.HasExited) child.Kill(); }
        Assert.False(global::WebSiteMonitor.RestartLauncher.CompleteHelperMode(["--restart-wait=invalid"]));
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token); }
    private sealed class NonSeekableBody(byte[] body) : MemoryStream(body)
    { public override bool CanSeek => false; }
    private class FixedFetcher(string html) : IHttpFetcher
    { public virtual Task<HttpFetchResult> FetchAsync(Uri uri, string? etag, string? modified, CancellationToken token) => Task.FromResult(new HttpFetchResult(200, Encoding.UTF8.GetBytes(html), "text/html", "utf-8", null, null, uri)); }
    private sealed class BlockingFetcher : FixedFetcher
    {
        public BlockingFetcher() : base("<p>OK</p>") { }
        public int Active; public int Count;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async Task<HttpFetchResult> FetchAsync(Uri uri, string? etag, string? modified, CancellationToken token)
        { Interlocked.Increment(ref Active); Interlocked.Increment(ref Count); Started.TrySetResult(); try { await Task.Delay(Timeout.Infinite, token); return await base.FetchAsync(uri, etag, modified, token); } finally { Interlocked.Decrement(ref Active); } }
    }
    private static T Get<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void PumpUntil(Func<bool> condition)
    { var watch = Stopwatch.StartNew(); while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(6)) { Application.DoEvents(); Thread.Sleep(5); } Assert.True(condition()); Application.DoEvents(); }
    private static void RunSta(Action action)
    { Exception? error = null; var thread = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } }); thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(15000)); if (error is not null) ExceptionDispatchInfo.Capture(error).Throw(); }
    public void Dispose() { SqliteConnection.ClearAllPools(); try { Directory.Delete(_root, true); } catch { } }
}
