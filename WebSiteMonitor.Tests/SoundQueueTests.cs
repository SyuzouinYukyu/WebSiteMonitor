using WebSiteMonitor.Core;

namespace WebSiteMonitor.Tests;

public sealed class SoundQueueTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WebSiteMonitorSoundQueue-" + Guid.NewGuid().ToString("N"));

    public SoundQueueTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public async Task ThreeConcurrentRequestsArePlayedInFifoOrderWithOnlyOneActiveSession()
    {
        var started = new List<string>();
        var active = 0;
        var maximum = 0;
        using var sound = new global::WebSiteMonitor.SoundService(async (request, cancellationToken) =>
        {
            lock (started)
            {
                started.Add(Path.GetFileName(request.Path));
                active++;
                maximum = Math.Max(maximum, active);
            }
            try { await Task.Delay(25, cancellationToken); return Completed(request); }
            finally { lock (started) active--; }
        });

        var a = sound.PlayAsync(Create("A.wav"), 80, new AppSettings());
        var b = sound.PlayAsync(Create("B.wav"), 80, new AppSettings());
        var c = sound.PlayAsync(Create("C.wav"), 80, new AppSettings());
        var results = await Task.WhenAll(a, b, c);

        Assert.Equal(["A.wav", "B.wav", "C.wav"], started);
        Assert.All(results, result => Assert.Equal(global::WebSiteMonitor.SoundPlaybackState.Completed, result.State));
        Assert.Equal(1, maximum);
        Assert.Equal(0, sound.ActivePlaybackCount);
        Assert.Equal(0, sound.PendingCount);
    }

    [Fact]
    public async Task FailedRequestDoesNotStopTheQueueAndMixedFormatsRemainFifo()
    {
        var started = new List<string>();
        using var sound = new global::WebSiteMonitor.SoundService((request, _) =>
        {
            lock (started) started.Add(Path.GetFileName(request.Path));
            if (request.Path.EndsWith("broken.mp3", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("FFmpeg decode failure");
            return Task.FromResult(Completed(request));
        });

        var midi = sound.PlayAsync(Create("first.mid"), 80, new AppSettings());
        var failed = sound.PlayAsync(Create("broken.mp3"), 80, new AppSettings());
        var wave = sound.PlayAsync(Create("third.wav"), 80, new AppSettings());
        var results = await Task.WhenAll(midi, failed, wave);

        Assert.Equal(["first.mid", "broken.mp3", "third.wav"], started);
        Assert.Equal(global::WebSiteMonitor.SoundPlaybackState.Completed, results[0].State);
        Assert.Equal(global::WebSiteMonitor.SoundPlaybackState.Failed, results[1].State);
        Assert.Equal(global::WebSiteMonitor.SoundPlaybackState.Completed, results[2].State);
        Assert.Equal(0, sound.ActiveMidiCount);
        Assert.Equal(0, sound.ActivePlaybackCount);
    }

    [Fact]
    public async Task TestStopCancelsOnlyTheCurrentTestRequest()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sound = new global::WebSiteMonitor.SoundService(async (request, cancellationToken) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Completed(request);
        });

        var resultTask = sound.PlayTestAsync(Create("test.wav"), 80, new AppSettings());
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(sound.StopTestPlayback());
        var result = await resultTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(global::WebSiteMonitor.SoundPlaybackState.Cancelled, result.State);
        Assert.Equal(0, sound.ActivePlaybackCount);
    }

    [Fact]
    public async Task QueueCapacityRejectsOverflowWithoutBlockingTheCaller()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sound = new global::WebSiteMonitor.SoundService(async (request, cancellationToken) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return Completed(request);
        }, capacity: 2);

        var first = sound.PlayAsync(Create("one.wav"), 80, new AppSettings());
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = sound.PlayAsync(Create("two.wav"), 80, new AppSettings());
        var third = sound.PlayAsync(Create("three.wav"), 80, new AppSettings());
        var overflow = await sound.PlayAsync(Create("four.wav"), 80, new AppSettings()).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(global::WebSiteMonitor.SoundPlaybackState.Failed, overflow.State);
        Assert.Contains("キューが満杯", overflow.Message);
        release.TrySetResult();
        Assert.All(await Task.WhenAll(first, second, third), result => Assert.Equal(global::WebSiteMonitor.SoundPlaybackState.Completed, result.State));
    }

    [Fact]
    public async Task ShutdownCancelsCurrentAndPendingRequestsAndRejectsNewRequests()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sound = new global::WebSiteMonitor.SoundService(async (request, cancellationToken) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Completed(request);
        });

        var current = sound.PlayAsync(Create("current.wav"), 80, new AppSettings());
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var pendingA = sound.PlayAsync(Create("pending-a.mid"), 80, new AppSettings());
        var pendingB = sound.PlayAsync(Create("pending-b.mp3"), 80, new AppSettings());
        await sound.ShutdownAsync(TimeSpan.FromSeconds(2));
        var afterShutdown = await sound.PlayAsync(Create("after.wav"), 80, new AppSettings());
        var results = await Task.WhenAll(current, pendingA, pendingB);

        Assert.All(results, result => Assert.Equal(global::WebSiteMonitor.SoundPlaybackState.Cancelled, result.State));
        Assert.Equal(global::WebSiteMonitor.SoundPlaybackState.Cancelled, afterShutdown.State);
        Assert.Equal(0, sound.ActivePlaybackCount);
        Assert.Equal(0, sound.PendingCount);
    }

    [Fact]
    public async Task AllRequestsCompleteExactlyOnceAndLeaveNoServiceSession()
    {
        var finals = new List<global::WebSiteMonitor.SoundPlaybackResult>();
        using var sound = new global::WebSiteMonitor.SoundService((request, _) => Task.FromResult(Completed(request)));
        sound.PlaybackStateChanged += result => { if (result.IsFinal) lock (finals) finals.Add(result); };

        var tasks = Enumerable.Range(0, 12).Select(index => sound.PlayAsync(Create($"sound-{index}.wav"), 80, new AppSettings())).ToArray();
        await Task.WhenAll(tasks);

        Assert.Equal(12, finals.Count);
        Assert.All(finals, result => Assert.Equal(global::WebSiteMonitor.SoundPlaybackState.Completed, result.State));
        Assert.Equal(0, sound.ActivePlaybackCount);
        Assert.Equal(0, sound.ActiveMidiCount);
        Assert.Equal(0, sound.PendingCount);
    }

    private string Create(string fileName)
    {
        var path = Path.Combine(_root, fileName);
        File.WriteAllBytes(path, [0]);
        return path;
    }

    private static global::WebSiteMonitor.SoundPlaybackResult Completed(global::WebSiteMonitor.SoundRequest request)
        => new(global::WebSiteMonitor.SoundPlaybackState.Completed, "テスト再生完了", null, request.Kind);
}
