using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using WebSiteMonitor.Core;

namespace WebSiteMonitor.Tests;

public sealed class CoreTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "WebSiteMonitorTests-" + Guid.NewGuid().ToString("N"));
    public CoreTests() => Directory.CreateDirectory(_temp);
    public void Dispose() { try { Directory.Delete(_temp, true); } catch { } }

    [Fact] public void Sha256ChangeDetection() { var a=ContentHasher.Sha256("a");Assert.Equal(a,ContentHasher.Sha256("a"));Assert.NotEqual(a,ContentHasher.Sha256("b"));Assert.Equal(CheckOutcome.Changed,UpdateDecision.Decide(a,ContentHasher.Sha256("b"))); }
    [Fact] public void FirstBaselineDoesNotNotify() { var hash=ContentHasher.Sha256("a");var outcome=UpdateDecision.Decide(null,hash);Assert.Equal(CheckOutcome.BaselineCreated,outcome);Assert.False(UpdateDecision.ShouldNotify(null,hash,outcome)); }
    [Fact] public void HtmlTextNormalization() { Assert.Equal("Hello world & all",ContentExtractor.ExtractText("<style>x</style><p>Hello\n world &amp; all</p><script>bad</script>")); }
    [Fact] public void CssSelectorExtraction() { Assert.Equal("A\nB",ContentExtractor.ExtractCss("<div class=x>A</div><div class=x>B</div>",".x")); }
    [Fact] public void XPathExtraction() { Assert.Equal("A\nB",ContentExtractor.ExtractXPath("<ul><li>A</li><li>B</li></ul>","//li")); }
    [Fact] public void RegexExtractionUsesCaptureOne() { Assert.Equal("12\n34",ContentExtractor.ExtractRegex("x12 y34",@"[xy](\d+)")); }
    [Fact] public void RegexTimeoutIsEnforced() { Assert.Throws<RegexMatchTimeoutException>(()=>ContentExtractor.ExtractRegex(new string('a',200000)+"!","^(a+)+$",TimeSpan.FromMilliseconds(1))); }
    [Fact] public void ParsesRss() { var value=FeedParser.ParseAndNormalize("<rss version='2.0'><channel><item><guid>1</guid><title>A</title><link>https://x/1</link><pubDate>D</pubDate></item></channel></rss>");Assert.Contains("1|A|https://x/1|D",value); }
    [Fact] public void ParsesAtom() { var value=FeedParser.ParseAndNormalize("<feed xmlns='http://www.w3.org/2005/Atom'><entry><id>1</id><title>A</title><link href='https://x/1'/><updated>D</updated></entry></feed>");Assert.Contains("1|A|https://x/1|D",value); }
    [Fact] public void FeedIdentityFallsBackToLink() { var a=FeedParser.ParseAndNormalize("<rss><channel><item><title>A</title><link>https://x/1</link></item></channel></rss>");Assert.StartsWith("https://x/1|",a); }
    [Fact] public void IntervalScheduleCalculates() { var now=DateTimeOffset.Now;var s=new Site{ScheduleMode=ScheduleMode.Interval,IntervalMinutes=10,LastChecked=now};Assert.Equal(now.AddMinutes(10),ScheduleCalculator.NextDue(s,now)); }
    [Fact] public void DailyScheduleIsFuture() { var now=DateTimeOffset.Now;var s=new Site{ScheduleMode=ScheduleMode.Daily,DailyTime=now.ToString("HH:mm")};Assert.True(ScheduleCalculator.NextDue(s,now)>now); }
    [Theory][InlineData(408,true)][InlineData(429,true)][InlineData(500,true)][InlineData(404,false)] public void RetryClassification(int status,bool expected)=>Assert.Equal(expected,RetryPolicy.IsTransient(status));
    [Fact] public void DuplicateNotificationSuppressed(){var h=ContentHasher.Sha256("a");Assert.False(UpdateDecision.ShouldNotify(h,h,CheckOutcome.Changed));Assert.True(UpdateDecision.ShouldNotify(null,h,CheckOutcome.Changed));}
    [Theory][InlineData("a.mp3",true)][InlineData("a.MIDI",true)][InlineData("a.exe",false)] public void SoundExtensionClassification(string path,bool expected)=>Assert.Equal(expected,global::WebSiteMonitor.SoundService.IsSupportedExtension(path));

    [Fact] public void DatabaseCrudAndSchema()
    {
        var db=NewDatabase();Assert.Equal(7,db.GetSchemaVersion());var s=ValidSite();var id=db.SaveSite(s);Assert.True(id>0);Assert.Equal("Test",db.GetSite(id)!.Name);s.Name="Changed";db.SaveSite(s);Assert.Equal("Changed",db.GetSite(id)!.Name);db.DeleteSite(id);Assert.Null(db.GetSite(id));
    }

    [Fact] public void DatabaseBaselineChangeHistoryAndNoDuplicate()
    {
        var db=NewDatabase();var s=ValidSite();db.SaveSite(s);var a=db.ApplySuccess(s.Id,"A","first",null,null,MonitorMode.Text,DateTimeOffset.Now);Assert.Equal(CheckOutcome.BaselineCreated,a.Outcome);Assert.False(a.ShouldNotify);var b=db.ApplySuccess(s.Id,"B","second",null,null,MonitorMode.Text,DateTimeOffset.Now);Assert.Equal(CheckOutcome.Changed,b.Outcome);Assert.True(b.ShouldNotify);db.MarkNotified(s.Id,"B");var same=db.ApplySuccess(s.Id,"B","second",null,null,MonitorMode.Text,DateTimeOffset.Now);Assert.Equal(CheckOutcome.Unchanged,same.Outcome);Assert.Single(db.GetHistory());
    }

    [Fact] public void HttpErrorKeepsBaseline()
    {
        var db=NewDatabase();var s=ValidSite();s.LastHash="OLD";s.LastPreview="old";db.SaveSite(s);db.ApplyError(s.Id,"network",DateTimeOffset.Now);var loaded=db.GetSite(s.Id)!;Assert.Equal("OLD",loaded.LastHash);Assert.Equal("old",loaded.LastPreview);Assert.Equal(1,loaded.ConsecutiveErrors);
    }

    [Fact] public void SettingsAtomicSaveAndCorruptRecovery()
    {
        var path=Path.Combine(_temp,"settings.json");var store=new SettingsStore(path);store.Save(new AppSettings{HistoryRetentionDays=99});Assert.Equal(99,store.Load().HistoryRetentionDays);Assert.False(File.Exists(path+".tmp"));File.WriteAllText(path,"{");var recovered=new SettingsStore(path);Assert.Equal(180,recovered.Load().HistoryRetentionDays);Assert.NotNull(recovered.RecoveryMessage);
    }

    [Fact] public async Task HttpFetcherHandlesEtagAnd304()
    {
        var handler=new StubHandler();using var client=new HttpClient(handler);using var fetcher=new SharedHttpFetcher(client);var result=await fetcher.FetchAsync(new Uri("https://example.test/"),"\"abc\"",null,CancellationToken.None);Assert.True(result.NotModified);Assert.Equal("\"abc\"",handler.SeenTag);
    }

    private Database NewDatabase(){var db=new Database(Path.Combine(_temp,Guid.NewGuid()+".db"));db.Initialize();return db;}
    private static Site ValidSite()=>new(){Name="Test",Url="https://example.test/",MonitorMode=MonitorMode.Text,ScheduleMode=ScheduleMode.Interval,IntervalMinutes=60,DailyTime="09:00"};
    private sealed class StubHandler:HttpMessageHandler{public string? SeenTag;protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken){SeenTag=request.Headers.IfNoneMatch.FirstOrDefault()?.ToString();return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified){RequestMessage=request});}}
}
