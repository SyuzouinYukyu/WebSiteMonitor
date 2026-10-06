using System.Drawing;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Windows.Forms;
using WebSiteMonitor.Core;

namespace WebSiteMonitor.Tests;

[Collection("V106 Windows Forms")]
public sealed class V115Tests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WebSiteMonitor-v115-tests-" + Guid.NewGuid().ToString("N"));

    public V115Tests() => Directory.CreateDirectory(_root);
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_root, true);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(18)]
    public void DetailInputLabelAndPasteButtonSwitchTogetherWithoutOverlapping(int fontSize)
    {
        Sta(() =>
        {
            using var sound = new global::WebSiteMonitor.SoundService();
            using var form = Editor(new AppSettings { UiFontSize = fontSize }, sound);
            form.Show();
            form.Size = form.MinimumSize;
            global::WebSiteMonitor.ScrollableDialogLayout.FitToWorkingArea(form, new Rectangle(0, 0, 1280, 672));
            Application.DoEvents();

            var mode = Field<ComboBox>(form, "_mode");
            var inputs = new[]
            {
                Field<TextBox>(form, "_feed"),
                Field<TextBox>(form, "_selector"),
                Field<TextBox>(form, "_xpath"),
                Field<TextBox>(form, "_regex")
            };
            var labels = Field<Dictionary<Control, Label>>(form, "_detailLabels");
            var rows = Field<Dictionary<Control, Control>>(form, "_detailFields");
            foreach (var selected in new[] { 1, 4, 5, 6, 0, 2, 3, 6, 1, 0 })
            {
                mode.SelectedIndex = selected;
                Application.DoEvents();
                var active = selected switch { 1 => 0, 4 => 1, 5 => 2, 6 => 3, _ => -1 };
                for (var index = 0; index < inputs.Length; index++)
                {
                    var box = inputs[index];
                    var row = rows[box];
                    var paste = row.Controls.OfType<Button>().Single();
                    var expected = index == active;
                    Assert.Equal(expected, box.Visible);
                    Assert.Equal(expected, labels[box].Visible);
                    Assert.Equal(expected, row.Visible);
                    Assert.Equal(expected, paste.Visible);
                    if (!expected) continue;
                    Assert.True(box.Width >= 60, $"mode={selected}, font={fontSize}, box={box.Width}");
                    Assert.True(box.Right <= paste.Left, $"mode={selected}: input overlaps paste button");
                    Assert.True(paste.Right <= row.ClientSize.Width, $"mode={selected}: paste button exceeds row");
                    Assert.True(paste.Width >= paste.GetPreferredSize(Size.Empty).Width);
                }
                Assert.Equal(active < 0 ? 3 : 4, All<Button>(form).Count(b => b.Text == "貼り付け" && b.Visible));
            }

            var footer = All<FlowLayoutPanel>(form).Single(panel => panel.Name == "FixedFooter");
            Assert.All(footer.Controls.OfType<Button>(), button => Assert.True(button.Visible && button.Width >= button.GetPreferredSize(Size.Empty).Width));
            form.Close();
        });
    }

    [Fact]
    public void PastePreservesExpressionsAndSavedRegexStillPreviewsAndReloads()
    {
        Sta(() =>
        {
            var db = new Database(Path.Combine(_root, "site.db"));
            db.Initialize();
            using var sound = new global::WebSiteMonitor.SoundService();
            using var form = Editor(new AppSettings(), sound, db);
            form.Show();
            Application.DoEvents();
            Field<TextBox>(form, "_name").Text = "貼り付け確認";
            Field<TextBox>(form, "_url").Text = "https://example.test/";
            var mode = Field<ComboBox>(form, "_mode");

            void Paste(int modeIndex, string fieldName, string clipboard, string expected)
            {
                mode.SelectedIndex = modeIndex;
                var target = Field<TextBox>(form, fieldName);
                form.ReadClipboardText = () => clipboard;
                var button = target.Parent!.Controls.OfType<Button>().Single();
                button.PerformClick();
                Assert.Equal(expected, target.Text);
                Assert.True(target.Focused);
                Assert.Equal(expected.Length, target.SelectionStart);
                form.ReadClipboardText = () => null;
                button.PerformClick();
                Assert.Equal(expected, target.Text);
            }

            Paste(1, "_feed", " \r\nhttps://example.test/feed\n ", "https://example.test/feed");
            Paste(4, "_selector", "article[data-x=\"a+b\"] > a:nth-child(2)\r\n", "article[data-x=\"a+b\"] > a:nth-child(2)");
            Paste(5, "_xpath", "//a[contains(., 'HiP2P') and contains(., 'windows')]\r\n", "//a[contains(., 'HiP2P') and contains(., 'windows')]");
            const string regex = "\"tag_name\"\\s*:\\s*\"v?([^\"]+)\"";
            Paste(6, "_regex", regex + "\r\n", regex);

            var preview = form.RunMonitorTestAsync();
            var until = DateTime.UtcNow.AddSeconds(8);
            while (!preview.IsCompleted && DateTime.UtcNow < until)
            {
                Application.DoEvents();
                Thread.Sleep(10);
            }
            Assert.True(preview.IsCompleted, "monitor preview timed out");
            preview.GetAwaiter().GetResult();
            Application.DoEvents();
            Assert.Contains("1.2.3", Field<TextBox>(form, "_preview").Text);

            All<Button>(form).Single(button => button.Text == "保存").PerformClick();
            Assert.Equal(DialogResult.OK, form.DialogResult);
            Assert.Equal(MonitorMode.Regex, form.Value.MonitorMode);
            Assert.Equal(regex, form.Value.Regex);
            Assert.Equal("//a[contains(., 'HiP2P') and contains(., 'windows')]", form.Value.XPath);
            using var reopened = Editor(new AppSettings(), sound, db, form.Value);
            reopened.Show();
            Application.DoEvents();
            Assert.Equal(regex, Field<TextBox>(reopened, "_regex").Text);
            Assert.True(Field<TextBox>(reopened, "_regex").Visible);
            reopened.Close();
        });
    }

    private global::WebSiteMonitor.SiteEditForm Editor(AppSettings settings, global::WebSiteMonitor.SoundService sound,
        Database? database = null, Site? source = null)
    {
        database ??= new Database(Path.Combine(_root, Guid.NewGuid().ToString("N") + ".db"));
        database.Initialize();
        return new global::WebSiteMonitor.SiteEditForm(source,
            new MonitorEngine(database, new FixedFetcher(), new FileLogger(Path.Combine(_root, "logs"))),
            sound, settings, Path.Combine(_root, "sounds"));
    }

    private static T Field<T>(object target, string name)
        => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    private static IEnumerable<T> All<T>(Control parent) where T : Control
    {
        foreach (Control child in parent.Controls)
        {
            if (child is T typed) yield return typed;
            foreach (var nested in All<T>(child)) yield return nested;
        }
    }

    private static void Sta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "STA UI test timed out");
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    private sealed class FixedFetcher : IHttpFetcher
    {
        public Task<HttpFetchResult> FetchAsync(Uri uri, string? etag, string? modified, CancellationToken cancellationToken)
            => Task.FromResult(new HttpFetchResult(200, Encoding.UTF8.GetBytes("{\"tag_name\":\"v1.2.3\"}"),
                "text/html", "utf-8", null, null, uri));
    }
}
