using System.Buffers.Binary;
using System.Drawing;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Forms;
using Microsoft.Data.Sqlite;
using WebSiteMonitor.Core;

namespace WebSiteMonitor.Tests;

[Collection("V106 Windows Forms")]
public sealed class V116Tests : IDisposable
{
    private const string Password = "v116-test-password";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WebSiteMonitor-v116-" + Guid.NewGuid().ToString("N"));
    public V116Tests() => Directory.CreateDirectory(_root);
    public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(_root, true); }
    private Database Db() { var db = new Database(Path.Combine(_root, Guid.NewGuid() + ".db")); db.Initialize(); return db; }

    [Fact]
    public void SettingsAndEncryptedFormatOneRoundTripWithLegacyDefaultsAndStrictValidation()
    {
        Assert.Equal(3, new AppSettings().ConsecutiveErrorAlertThreshold);
        var path = Path.Combine(_root, "settings.json");
        var store = new SettingsStore(path);
        var db = Db();
        foreach (var threshold in new[] { 0, 1, 3, 9999 })
        {
            var settings = new AppSettings { ConsecutiveErrorAlertThreshold = threshold };
            store.Save(settings);
            Assert.Equal(threshold, store.Load().ConsecutiveErrorAlertThreshold);
            var doc = ConfigurationTransfer.Capture([], settings);
            var export = Path.Combine(_root, "設定.wsmcfg");
            ConfigurationTransfer.Export(export, doc, Password);
            var bytes = File.ReadAllBytes(export);
            Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8, 4)));
            Assert.Equal(200000, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12, 4)));
            var restored = ConfigurationTransfer.Read(export, Password);
            Assert.Equal(threshold, restored.AppSettings.ConsecutiveErrorAlertThreshold);
            ConfigurationTransfer.Import(restored, ConfigurationImportMode.Merge, db, store);
            Assert.Equal(threshold, store.Load().ConsecutiveErrorAlertThreshold);
        }
        var old = JsonNode.Parse(JsonSerializer.Serialize(new AppSettings()))!.AsObject();
        old.Remove(nameof(AppSettings.ConsecutiveErrorAlertThreshold));
        File.WriteAllText(path, old.ToJsonString());
        Assert.Equal(3, store.Load().ConsecutiveErrorAlertThreshold);
        Assert.Null(store.RecoveryMessage);
        var raw = JsonNode.Parse(JsonSerializer.Serialize(ConfigurationTransfer.Capture([], new AppSettings())))!;
        raw["AppSettings"]!.AsObject().Remove(nameof(AppSettings.ConsecutiveErrorAlertThreshold));
        Assert.Equal(3, ConfigurationTransfer.Decrypt(EncryptRaw(raw), Password).AppSettings.ConsecutiveErrorAlertThreshold);
        foreach (var invalid in new[] { -1, 10000, int.MaxValue })
        {
            raw["AppSettings"]![nameof(AppSettings.ConsecutiveErrorAlertThreshold)] = invalid;
            Assert.Throws<ConfigurationException>(() => ConfigurationTransfer.Decrypt(EncryptRaw(raw), Password));
            Assert.Throws<ConfigurationException>(() => ConfigurationTransfer.Encrypt(ConfigurationTransfer.Capture([], new AppSettings { ConsecutiveErrorAlertThreshold = invalid }), Password));
            File.WriteAllText(path, JsonSerializer.Serialize(new AppSettings { ConsecutiveErrorAlertThreshold = invalid }));
            Assert.Equal(Math.Clamp(invalid, 0, 9999), store.Load().ConsecutiveErrorAlertThreshold);
        }
        // The compatibility exception must not weaken the existing strict field checks.
        raw["AppSettings"]!.AsObject().Remove(nameof(AppSettings.ConsecutiveErrorAlertThreshold));
        raw["AppSettings"]!.AsObject().Remove(nameof(AppSettings.NotificationsEnabled));
        Assert.Throws<ConfigurationException>(() => ConfigurationTransfer.Decrypt(EncryptRaw(raw), Password));
        Assert.Equal(7, db.GetSchemaVersion());
    }

    [Fact]
    public void SettingsTextBoxSaveValidatesBeforeChangingSettingsAndSelectsInvalidInput()
    {
        Sta(() =>
        {
            var settings = new AppSettings();
            using var form = new global::WebSiteMonitor.SettingsForm(settings);
            form.Show(); Application.DoEvents();
            var box = All<TextBox>(form).Single(b => b.Name == "ConsecutiveErrorAlertThreshold");
            Assert.Equal(0, box.MaxLength);
            foreach (var value in new[] { "0", "1", "3", "9999" })
            {
                box.Text = value;
                Assert.True(form.TrySave(_ => Assert.Fail("valid value rejected")));
                Assert.Equal(int.Parse(value), settings.ConsecutiveErrorAlertThreshold);
            }
            var before = JsonSerializer.Serialize(settings);
            foreach (var value in new[] { "３", "abc", "!", " ", "3 ", "1.0", "+3", "-1", "3\t", "1\n2", "999９" })
                Reject(value, "半角数字のみ入力可能です");
            foreach (var value in new[] { "", "10000", "999999999999999999999999999" })
                Reject(value, "0～9999の範囲で入力してください。");
            void Reject(string input, string expected)
            {
                box.Text = input;
                var actual = box.Text; // Single-line TextBox may normalize pasted line breaks.
                var message = "";
                Assert.False(form.TrySave(text => message = text));
                Assert.Equal(expected, message);
                Assert.Equal(before, JsonSerializer.Serialize(settings));
                Assert.True(box.Focused);
                Assert.Equal(actual.Length, box.SelectionLength);
                Assert.Equal(actual, box.Text);
                Assert.NotEqual(DialogResult.OK, form.DialogResult);
            }
            box.Text = "1";
            All<Button>(form).Single(b => b.Text == "保存").PerformClick();
            Assert.Equal(DialogResult.OK, form.DialogResult);
            Assert.Equal(1, settings.ConsecutiveErrorAlertThreshold);
        });
    }

    [Fact]
    public async Task CommittedErrorsWarnOnlyAtThresholdThenResetAndPreviewDoesNotCount()
    {
        var db = Db();
        var site = new Site { Name = "安全なサイト", Url = "https://example.test/?token=URL_SECRET", MonitorMode = MonitorMode.Text };
        db.SaveSite(site);
        var fetcher = new SwitchingFetcher();
        var settings = new AppSettings { NotificationsEnabled = false };
        var engine = new MonitorEngine(db, fetcher, new FileLogger(Path.Combine(_root, "logs")), () => settings.NotificationsEnabled);
        for (var episode = 0; episode < 2; episode++)
        {
            for (var count = 1; count <= 5; count++)
            {
                var result = await engine.CheckAsync(site, CancellationToken.None);
                Assert.Equal(CheckOutcome.Failed, result.Outcome);
                Assert.Equal(count - 1, result.Site.ConsecutiveErrors); // Prove the original snapshot is stale.
                Assert.Equal(count, db.GetSite(site.Id)!.ConsecutiveErrors);
                Assert.Equal(count, result.CommittedConsecutiveErrors);
                var warning = global::WebSiteMonitor.ConsecutiveErrorAlert.CreateMessage(result, 3);
                Assert.Equal(count == 3, warning is not null);
                Assert.Null(global::WebSiteMonitor.ConsecutiveErrorAlert.CreateMessage(result, 0));
                if (warning is not null)
                {
                    Assert.Contains("3回連続", warning);
                    Assert.DoesNotContain("URL_SECRET", warning);
                    Assert.DoesNotContain("EXCEPTION_SECRET", warning);
                    Assert.DoesNotContain("https://", warning);
                }
                foreach (var outcome in Enum.GetValues<CheckOutcome>().Where(o => o != CheckOutcome.Failed))
                    Assert.Null(global::WebSiteMonitor.ConsecutiveErrorAlert.CreateMessage(result with { Outcome = outcome }, 3));
            }
            Assert.Null(global::WebSiteMonitor.ConsecutiveErrorAlert.CreateMessage(new CheckResult(CheckOutcome.Failed, site, ""), 2));
            var before = db.GetSite(site.Id)!.ConsecutiveErrors;
            await Assert.ThrowsAsync<HttpRequestException>(() => engine.TestExtractAsync(site, CancellationToken.None));
            Assert.Equal(before, db.GetSite(site.Id)!.ConsecutiveErrors);
            fetcher.Fail = false;
            await engine.TestExtractAsync(site, CancellationToken.None);
            Assert.Equal(before, db.GetSite(site.Id)!.ConsecutiveErrors);
            var success = await engine.CheckAsync(site, CancellationToken.None);
            Assert.NotEqual(CheckOutcome.Failed, success.Outcome);
            Assert.Null(success.CommittedConsecutiveErrors);
            Assert.Equal(0, db.GetSite(site.Id)!.ConsecutiveErrors);
            fetcher.Fail = true;
        }
        // One site's failures do not affect another site's threshold.
        var other = new Site { Name = "別サイト", Url = "https://other.test/", MonitorMode = MonitorMode.Text };
        db.SaveSite(other);
        var first = await engine.CheckAsync(other, CancellationToken.None);
        Assert.NotNull(global::WebSiteMonitor.ConsecutiveErrorAlert.CreateMessage(first, 1));
        Assert.Null(global::WebSiteMonitor.ConsecutiveErrorAlert.CreateMessage(first, 3));
        Assert.Equal(0, db.GetSite(site.Id)!.ConsecutiveErrors);
        Assert.Equal(7, db.GetSchemaVersion());
    }

    [Fact]
    public async Task DeferredResultsKeepTheirOwnThresholdAfterDatabaseAdvances()
    {
        var db = Db();
        var site = new Site { Name = "遅延完了確認", Url = "https://example.test/", MonitorMode = MonitorMode.Text };
        db.SaveSite(site);
        var engine = new MonitorEngine(db, new SwitchingFetcher(), new FileLogger(Path.Combine(_root, "race-logs")));
        var results = new List<CheckResult>();
        for (var i = 0; i < 4; i++) results.Add(await engine.CheckAsync(site, CancellationToken.None));
        Assert.Equal(4, db.GetSite(site.Id)!.ConsecutiveErrors);
        Assert.Equal(new int?[] { 1, 2, 3, 4 }, results.Select(r => r.CommittedConsecutiveErrors));
        // Handle completions only after later failures have advanced the DB.
        Assert.NotNull(global::WebSiteMonitor.ConsecutiveErrorAlert.CreateMessage(results[0], 1));
        foreach (var threshold in new[] { 1, 2, 3 })
            Assert.Equal(Enumerable.Range(1, 4).Select(n => n == threshold),
                results.Select(result => global::WebSiteMonitor.ConsecutiveErrorAlert.CreateMessage(result, threshold) is not null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureAndDeadlineResultsCommitCountsButRevisionMismatchIsDiscarded(bool deadline)
    {
        var db = Db();
        var site = new Site { Name = "確定値確認", Url = "https://example.test/", MonitorMode = MonitorMode.Text };
        db.SaveSite(site);
        var fetcher = new RevisionFailureFetcher(deadline);
        var engine = new MonitorEngine(db, fetcher, new FileLogger(Path.Combine(_root, "revision-logs")), monitorTimeout: TimeSpan.FromMilliseconds(100));
        var failed = await engine.CheckAsync(site, CancellationToken.None);
        Assert.Equal(CheckOutcome.Failed, failed.Outcome);
        Assert.Equal(1, failed.CommittedConsecutiveErrors);
        Assert.NotNull(global::WebSiteMonitor.ConsecutiveErrorAlert.CreateMessage(failed, 1));
        fetcher.BeforeFailure = () =>
        {
            var changed = db.GetSite(site.Id)!;
            changed.Url = "https://example.test/changed";
            db.SaveSite(changed);
        };
        var discarded = await engine.CheckAsync(site, CancellationToken.None);
        Assert.Equal(CheckOutcome.Discarded, discarded.Outcome);
        Assert.Null(discarded.CommittedConsecutiveErrors);
        Assert.Null(global::WebSiteMonitor.ConsecutiveErrorAlert.CreateMessage(discarded, 1));
        // Editing monitoring settings already resets the DB counter; a discarded result must not increment it.
        Assert.Equal(0, db.GetSite(site.Id)!.ConsecutiveErrors);
        Assert.Equal(1, failed.CommittedConsecutiveErrors);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(18)]
    public void SettingsAndAboutControlsFitAtNormalAndReducedSizes(int fontSize)
    {
        Sta(() =>
        {
            global::WebSiteMonitor.UiFontManager.Initialize(new AppSettings { UiFontSize = fontSize });
            using var settings = new global::WebSiteMonitor.SettingsForm(new AppSettings { UiFontSize = fontSize });
            using var about = new global::WebSiteMonitor.AboutForm();
            foreach (var form in new Form[] { settings, about })
            {
                form.Show(); Application.DoEvents();
                foreach (var small in new[] { false, true })
                {
                    if (small) form.Size = form.MinimumSize;
                    if (form == settings) global::WebSiteMonitor.ScrollableDialogLayout.FitToWorkingArea(form, new Rectangle(0, 0, 1000, 600));
                    Application.DoEvents(); form.PerformLayout();
                    foreach (var control in All<Control>(form).Where(c => c.Visible && (c is Label or Button || c is TextBox && c.Name == "ConsecutiveErrorAlertThreshold")))
                    {
                        if (control is Label or Button || control is TextBox { Multiline: false })
                        {
                            var preferred = control.GetPreferredSize(new Size(control.Width, 0));
                            Assert.True(control.Height >= preferred.Height, $"height: {control.Text} {control.Size} preferred {preferred}");
                            Assert.True(control.Width >= preferred.Width, $"width: {control.Text} {control.Size} preferred {preferred}");
                        }
                        Assert.True(control.Right <= control.Parent!.ClientSize.Width, $"right: {control.Text}");
                        Assert.True(control.Bottom <= control.Parent!.ClientSize.Height, $"bottom: {control.Text}");
                        foreach (Control sibling in control.Parent.Controls)
                            if (sibling != control && sibling.Visible) Assert.False(control.Bounds.IntersectsWith(sibling.Bounds), $"overlap: {control.Text} / {sibling.Text}");
                    }
                    foreach (var button in All<Button>(form).Where(b => b.Text is "保存" or "キャンセル" or "バージョン情報 / 第三者ライセンス" or "閉じる"))
                        Assert.True(form.ClientRectangle.Contains(form.RectangleToClient(button.RectangleToScreen(button.ClientRectangle))), $"footer outside form: {button.Text}");
                    if (form == settings)
                    {
                        var viewport = All<Panel>(form).Single(p => p.Name == "BodyViewport");
                        var box = All<TextBox>(form).Single(b => b.Name == "ConsecutiveErrorAlertThreshold");
                        viewport.ScrollControlIntoView(box); Application.DoEvents();
                        Assert.True(viewport.ClientRectangle.Contains(viewport.RectangleToClient(box.RectangleToScreen(box.ClientRectangle))));
                    }
                }
            }
            var github = All<LinkLabel>(about).Single();
            Assert.Equal("GitHub: https://github.com/SyuzouinYukyu/WebSiteMonitor", github.Text);
            Assert.Equal(global::WebSiteMonitor.AboutForm.GitHubUrl, github.Links[0].LinkData);
            var start = global::WebSiteMonitor.BrowserLaunch.CreateStartInfo(global::WebSiteMonitor.AboutForm.GitHubUrl)!;
            Assert.Equal(global::WebSiteMonitor.AboutForm.GitHubUrl, start.FileName);
            Assert.True(start.UseShellExecute);
            Assert.Contains("FFmpeg", All<TextBox>(about).Single().Text);
            // Do not click the link or launch a real browser in automated tests.
        });
    }

    private static IEnumerable<T> All<T>(Control parent) where T : Control
    {
        foreach (Control child in parent.Controls) { if (child is T typed) yield return typed; foreach (var nested in All<T>(child)) yield return nested; }
    }
    private static void Sta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(25)), "STA timeout");
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
    private sealed class SwitchingFetcher : IHttpFetcher
    {
        internal bool Fail = true;
        public Task<HttpFetchResult> FetchAsync(Uri uri, string? etag, string? modified, CancellationToken token)
            => Fail ? throw new HttpRequestException("EXCEPTION_SECRET") : Task.FromResult(new HttpFetchResult(200, Encoding.UTF8.GetBytes("<html>normal</html>"), "text/html", "utf-8", null, null, uri));
    }
    private sealed class RevisionFailureFetcher(bool deadline) : IHttpFetcher
    {
        internal Action? BeforeFailure;
        public async Task<HttpFetchResult> FetchAsync(Uri uri, string? etag, string? modified, CancellationToken token)
        {
            BeforeFailure?.Invoke();
            if (deadline) await Task.Delay(Timeout.Infinite, token);
            throw new HttpRequestException("test failure");
        }
    }
    private static byte[] EncryptRaw(JsonNode document)
    {
        var plain = Encoding.UTF8.GetBytes(document.ToJsonString());
        var bytes = new byte[64 + plain.Length]; Encoding.ASCII.GetBytes("WSMCFG01").CopyTo(bytes, 0);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8, 4), 1); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12, 4), 200000);
        RandomNumberGenerator.Fill(bytes.AsSpan(16, 16)); RandomNumberGenerator.Fill(bytes.AsSpan(32, 12));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(44, 4), plain.Length);
        var key = Rfc2898DeriveBytes.Pbkdf2(Password, bytes.AsSpan(16, 16), 200000, HashAlgorithmName.SHA256, 32);
        using var aes = new AesGcm(key, 16); aes.Encrypt(bytes.AsSpan(32, 12), plain, bytes.AsSpan(64), bytes.AsSpan(48, 16), bytes.AsSpan(0, 48));
        CryptographicOperations.ZeroMemory(key); return bytes;
    }
}
