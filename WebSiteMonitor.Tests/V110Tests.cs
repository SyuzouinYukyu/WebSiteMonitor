using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using Microsoft.Data.Sqlite;
using WebSiteMonitor.Core;

namespace WebSiteMonitor.Tests;

[Collection("V106 Windows Forms")]
public sealed class V110Tests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WebSiteMonitorV110-" + Guid.NewGuid().ToString("N"));
    public V110Tests() => Directory.CreateDirectory(_root);
    private Database Db() { var db = new Database(Path.Combine(_root, "test.db")); db.Initialize(); return db; }
    private MonitorEngine Engine(Database db) => new(db, new EmptyFetcher(), new FileLogger(Path.Combine(_root, "logs")));

    [Fact]
    public void PathResolutionHandlesJapaneseSpacesQuotesAndRejectsImplicitCurrentOrRelativeDirectories()
    {
        var directory = Path.Combine(_root, "日本語 FFmpeg"); Directory.CreateDirectory(directory);
        var exe = Path.Combine(directory, "ffmpeg.exe"); File.WriteAllText(exe, "non-executable resolver placeholder");
        Assert.Equal(exe, global::WebSiteMonitor.FfmpegBackend.ResolveExecutable($";relative;.;\"bad\"\"entry\";\"{directory}\";"));
        Assert.Equal(exe, global::WebSiteMonitor.FfmpegBackend.ResolveExecutable(directory));
        foreach (var path in new[] { "", ";;;", ".", "relative", "\".\"", @"\root-relative", Path.Combine(_root, "missing") })
        {
            var error = Assert.Throws<FileNotFoundException>(() => global::WebSiteMonitor.FfmpegBackend.ResolveExecutable(path));
            Assert.Contains("PATH", error.Message);
        }
        Assert.Throws<ArgumentException>(() => new global::WebSiteMonitor.FfmpegPcmWaveStream("tone.wav", "ffmpeg.exe"));
    }

    [Fact]
    public async Task MissingExternalFfmpegFailsOnlyAudioAndDoesNotStopFollowingQueueOrMonitoring()
    {
        var input = Path.Combine(_root, "missing.mp3"); File.WriteAllText(input, "test");
        using var sound = new global::WebSiteMonitor.SoundService((request, _) =>
        {
            if (request.Path == input) global::WebSiteMonitor.FfmpegBackend.ResolveExecutable("");
            return Task.FromResult(new global::WebSiteMonitor.SoundPlaybackResult(global::WebSiteMonitor.SoundPlaybackState.Completed, "完了"));
        });
        var failed = await sound.PlayAsync(input, 100, new AppSettings());
        Assert.True(failed.IsFailure); Assert.Contains("PATH", failed.Message);
        var other = Path.Combine(_root, "following.pcm"); File.WriteAllText(other, "test");
        Assert.Equal(global::WebSiteMonitor.SoundPlaybackState.Completed, (await sound.PlayAsync(other, 100, new AppSettings())).State);
        var db = Db(); var site = new Site { Name = "継続検証", Url = "https://example.test/", MonitorMode = MonitorMode.Text }; db.SaveSite(site);
        Assert.Equal(CheckOutcome.BaselineCreated, (await Engine(db).CheckAsync(site, default)).Outcome);
    }

    [Fact]
    public void ModelAndEditorDefaultsAre100AndOnWhileSavedExplicitValuesSurviveEditingAndReload()
    {
        Assert.Equal(100, new Site().SoundVolume); Assert.True(new Site().UpdateDialogNotification);
        RunSta(() =>
        {
            var db = Db(); var engine = Engine(db); using var sound = new global::WebSiteMonitor.SoundService();
            using var add = new global::WebSiteMonitor.SiteEditForm(null, engine, sound, new AppSettings(), _root);
            Assert.Equal(100, Get<NumericUpDown>(add, "_volume").Value); Assert.True(Get<CheckBox>(add, "_updateDialog").Checked);
            foreach (var volume in new[] { 0, 37, 100 }) foreach (var dialog in new[] { false, true })
            {
                var site = new Site { Name = "保存値", Url = "https://example.test/", SoundVolume = volume, UpdateDialogNotification = dialog }; db.SaveSite(site);
                var saved = new Database(Path.Combine(_root, "test.db")).GetSite(site.Id)!;
                using var edit = new global::WebSiteMonitor.SiteEditForm(saved, engine, sound, new AppSettings(), _root);
                Assert.Equal(volume, Get<NumericUpDown>(edit, "_volume").Value); Assert.Equal(dialog, Get<CheckBox>(edit, "_updateDialog").Checked);
                var values = (Site)typeof(global::WebSiteMonitor.SiteEditForm).GetMethod("ReadValues", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(edit, null)!;
                Assert.Equal(volume, values.SoundVolume); Assert.Equal(dialog, values.UpdateDialogNotification);
                Get<NumericUpDown>(edit, "_volume").Value = 100;
                Get<CheckBox>(edit, "_updateDialog").Checked = !dialog;
                values = (Site)typeof(global::WebSiteMonitor.SiteEditForm).GetMethod("ReadValues", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(edit, null)!;
                Assert.Equal(100, values.SoundVolume); Assert.Equal(!dialog, values.UpdateDialogNotification);
            }
        });
    }

    [Theory]
    [InlineData(10)]
    [InlineData(18)]
    public void ConfigurationDialogsAndMainMenuHaveUsableMaskedInputsAndUnclippedActions(int fontSize)
    {
        RunSta(() =>
        {
            using var password = new global::WebSiteMonitor.ConfigurationPasswordDialog(true, fontSize);
            using var import = new global::WebSiteMonitor.ConfigurationImportDialog(3, fontSize);
            foreach (var form in new Form[] { password, import })
            {
                form.Show(); form.PerformLayout(); Application.DoEvents();
                foreach (var control in Descendants(form).Where(c => c is Button or Label or RadioButton or TextBox))
                {
                    Assert.True(control.Width > 0 && control.Height > 0);
                    if (control is Label)
                    {
                        var size = TextRenderer.MeasureText(control.Text, control.Font, new Size(control.Width, 10000), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
                        Assert.True(control.Width >= size.Width && control.Height >= size.Height, $"{control.Text}: bounds={control.Size}, measured={size}, font={control.Font.Size}");
                        Assert.True(control.Right <= control.Parent!.ClientSize.Width, control.Text);
                    }
                    else if (control is Button or RadioButton)
                        Assert.True(control.Width >= TextRenderer.MeasureText(control.Text, control.Font, new Size(10000, 10000), TextFormatFlags.NoPadding).Width, control.Text);
                    if (control is Button) Assert.True(form.ClientRectangle.Contains(form.PointToClient(control.PointToScreen(Point.Empty))));
                }
                form.Hide();
            }
            Assert.Equal(2, Descendants(password).OfType<TextBox>().Count(b => b.UseSystemPasswordChar));
            Assert.Equal(ConfigurationImportMode.Merge, import.Mode);
            var db = Db(); var engine = Engine(db); using var scheduler = new global::WebSiteMonitor.OneShotScheduler(db, engine); using var sound = new global::WebSiteMonitor.SoundService();
            using var main = new global::WebSiteMonitor.MainForm(db, engine, scheduler, sound, () => new AppSettings { UiFontSize = fontSize }, _ => { }, () => { }, _ => { });
            main.Show(); Application.DoEvents();
            var menu = Descendants(main).OfType<ToolStrip>().SelectMany(s => s.Items.OfType<ToolStripDropDownButton>()).Single();
            Assert.Equal(new[] { "設定をエクスポート", "設定をインポート", "エクスポート用パスワードを変更", "記憶したパスワードを解除" }, menu.DropDownItems.Cast<ToolStripItem>().Select(i => i.Text));
            var restart = Descendants(main).OfType<ToolStrip>().SelectMany(strip => strip.Items.Cast<ToolStripItem>()).Single(item => item.Text == "再起動");
            Assert.False(restart.IsOnOverflow); Assert.True(restart.Available);
            Assert.True(restart.GetCurrentParent()!.ClientRectangle.Contains(restart.Bounds));
            main.Close();
        });
    }

    [Fact]
    public async Task ConfigurationSuspensionDrainsInFlightChecksBlocksNewChecksAndPreservesUserPause()
    {
        var db = Db(); var fetcher = new BlockingFetcher(); var engine = new MonitorEngine(db, fetcher, new FileLogger(Path.Combine(_root, "logs")));
        using var scheduler = new global::WebSiteMonitor.OneShotScheduler(db, engine);
        var site = new Site { Name = "監視", Url = "https://example.test/", MonitorMode = MonitorMode.Text }; db.SaveSite(site);
        scheduler.TogglePause();
        var running = scheduler.CheckNowAsync([site]); await fetcher.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var suspend = scheduler.SuspendForConfigurationAsync(); Assert.False(suspend.IsCompleted);
        await scheduler.CheckNowAsync([site]); Assert.Equal(1, fetcher.Count);
        fetcher.Release.SetResult(); using (await suspend) { await running; Assert.Equal(0, scheduler.RunningCount); }
        Assert.True(scheduler.Paused);
    }

    private static T Get<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target)!;
    private static IEnumerable<Control> Descendants(Control root) => root.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(Descendants(c)));
    private static void RunSta(Action work)
    {
        Exception? error = null; var thread = new Thread(() => { try { work(); } catch (Exception ex) { error = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(15000));
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
    private class EmptyFetcher : IHttpFetcher
    {
        public virtual Task<HttpFetchResult> FetchAsync(Uri uri, string? etag, string? modified, CancellationToken cancellationToken)
            => Task.FromResult(new HttpFetchResult(200, EncodingBytes, "text/html", "utf-8", null, null, uri));
        private static readonly byte[] EncodingBytes = System.Text.Encoding.UTF8.GetBytes("<html><body>baseline</body></html>");
    }
    private sealed class BlockingFetcher : EmptyFetcher
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Count;
        public override async Task<HttpFetchResult> FetchAsync(Uri uri, string? etag, string? modified, CancellationToken cancellationToken)
        { Interlocked.Increment(ref Count); Started.SetResult(); await Release.Task.WaitAsync(cancellationToken); return await base.FetchAsync(uri, etag, modified, cancellationToken); }
    }
    public void Dispose() { SqliteConnection.ClearAllPools(); try { Directory.Delete(_root, true); } catch { } }
}
