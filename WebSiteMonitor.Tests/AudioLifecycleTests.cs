namespace WebSiteMonitor.Tests;

public sealed class AudioLifecycleTests
{
    [Fact]
    public async Task MissingAudioProducesAnExplicitAsyncFailure()
    {
        using var sound = new global::WebSiteMonitor.SoundService();
        var result = await sound.PlayAsync(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".mp3"), 80, new WebSiteMonitor.Core.AppSettings());
        Assert.Equal(global::WebSiteMonitor.SoundPlaybackState.Failed, result.State);
        Assert.Contains("通知音", result.Message);
    }

    [Fact]
    public void PlaybackStoppedExceptionIsReportedAsFailure()
    {
        var result = global::WebSiteMonitor.SoundService.PlaybackStoppedResult(new InvalidOperationException("output device error"));
        Assert.Equal(global::WebSiteMonitor.SoundPlaybackState.Failed, result.State);
        Assert.IsType<InvalidOperationException>(result.Exception);
        Assert.Contains("output device error", result.Message);
    }

    [Fact]
    public async Task MidiCompletionClosesItsMciDevice()
    {
        var temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".mid");
        File.WriteAllBytes(temp, [0]);
        var commands = new List<string>();
        try
        {
            using var sound = new global::WebSiteMonitor.SoundService(command =>
            {
                lock (commands) commands.Add(command);
                return command.StartsWith("status ", StringComparison.OrdinalIgnoreCase) ? (0, "stopped") : (0, string.Empty);
            });
            var result = await sound.PlayAsync(temp, 80, new WebSiteMonitor.Core.AppSettings());
            Assert.Equal(global::WebSiteMonitor.SoundPlaybackState.Completed, result.State);
            Assert.Equal(0, sound.ActiveMidiCount);
            Assert.Contains(commands, command => command.StartsWith("close WebSiteMonitorMidi", StringComparison.OrdinalIgnoreCase));
        }
        finally { try { File.Delete(temp); } catch { } }
    }
    [Fact]
    public void StopLeavesNoMidiSession()
    {
        using var sound = new global::WebSiteMonitor.SoundService();
        sound.Stop();
        Assert.Equal(0, sound.ActiveMidiCount);
    }
}