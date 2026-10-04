using System.Diagnostics;

namespace WebSiteMonitor.Tests;

public sealed class V104AudioTests : IClassFixture<FfmpegTestRuntime>, IDisposable
{
    private readonly FfmpegTestRuntime _runtime;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WebSiteMonitorV104Audio-" + Guid.NewGuid().ToString("N"));
    public V104AudioTests(FfmpegTestRuntime runtime) { _runtime = runtime; Directory.CreateDirectory(_root); }
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Theory]
    [InlineData(512)]
    [InlineData(1024)]
    [InlineData(4096)]
    public void ShortReadsFillLargePcmRequestWithoutSkippingOrRepeatingBytes(int chunkSize)
    {
        var pattern = Enumerable.Range(0, 26460 * 2 + 17).Select(i => (byte)(i % 251)).ToArray();
        using var source = new ChunkedStream(pattern, chunkSize);
        var output = new byte[26460];
        var first = global::WebSiteMonitor.FfmpegPcmWaveStream.FillPcmBuffer(source, output, 0, output.Length);
        Assert.Equal(output.Length, first);
        Assert.Equal(pattern[..output.Length], output);
        var second = global::WebSiteMonitor.FfmpegPcmWaveStream.FillPcmBuffer(source, output, 0, output.Length);
        Assert.Equal(output.Length, second);
        Assert.Equal(pattern[output.Length..(output.Length * 2)], output);
        var final = global::WebSiteMonitor.FfmpegPcmWaveStream.FillPcmBuffer(source, output, 0, output.Length);
        Assert.Equal(17, final);
        Assert.Equal(pattern[^17..], output[..17]);
        Assert.Equal(0, global::WebSiteMonitor.FfmpegPcmWaveStream.FillPcmBuffer(source, output, 0, output.Length));
    }

    [ExternalFfmpegFact]
    public void ExternalFfmpegStreamsFiveSecondPcmContinuously()
    {
        var input = Path.Combine(_root, "five-seconds.wav");
        var start = new ProcessStartInfo { FileName = _runtime.Executable, UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardError = true };
        foreach (var part in new[] { "-nostdin", "-y", "-loglevel", "error", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100:duration=5", "-c:a", "pcm_s16le", input })
            start.ArgumentList.Add(part);
        using (var process = Process.Start(start) ?? throw new InvalidOperationException("FFmpeg did not start"))
        {
            var error = process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(15000), "FFmpeg input generation timed out");
            Assert.True(process.ExitCode == 0, error);
        }
        using var stream = new global::WebSiteMonitor.FfmpegPcmWaveStream(input, _runtime.Executable);
        var buffer = new byte[26460];
        Assert.Equal(0, stream.Read(buffer, 0, 0));
        var total = 0;
        var blocks = 0;
        while (true)
        {
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            Assert.True(read == buffer.Length || total + read == 5 * 44100 * 2 * 2, "Only the final PCM block may be short.");
            Assert.Contains(buffer, value => value != 0);
            total += read;
            blocks++;
        }
        Assert.Equal(5 * 44100 * 2 * 2, total);
        Assert.True(blocks > 30);
    }

    [Fact]
    public void ProductFfmpegStartsDirectlyWithoutShellOrConsoleWindow()
    {
        var start = global::WebSiteMonitor.FfmpegPcmWaveStream.CreateStartInfo("ffmpeg.exe", "tone.wav");
        Assert.False(start.UseShellExecute);
        Assert.True(start.CreateNoWindow);
        Assert.Equal(ProcessWindowStyle.Hidden, start.WindowStyle);
        Assert.True(start.RedirectStandardOutput);
        Assert.True(start.RedirectStandardError);
        Assert.Equal("ffmpeg.exe", start.FileName);
        Assert.Equal("tone.wav", start.ArgumentList[4]);
        Assert.DoesNotContain(start.ArgumentList, value => value.Contains("cmd.exe", StringComparison.OrdinalIgnoreCase) || value.Contains("powershell.exe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExternalFfmpegResolutionDoesNotCreateCacheOrModifyTheExecutable()
    {
        var exe = Path.Combine(_root, "ffmpeg.exe");
        File.WriteAllText(exe, "resolver-only test placeholder; never executed");
        var originalTime = File.GetLastWriteTimeUtc(exe);
        Assert.Equal(exe, global::WebSiteMonitor.FfmpegBackend.ResolveExecutable(_root));
        Assert.Equal(exe, global::WebSiteMonitor.FfmpegBackend.ResolveExecutable(_root));
        Assert.Equal(originalTime, File.GetLastWriteTimeUtc(exe));
        Assert.Single(Directory.GetFiles(_root));
        Assert.Empty(Directory.GetDirectories(_root));
        Assert.DoesNotContain(typeof(global::WebSiteMonitor.SoundService).Assembly.GetManifestResourceNames(),
            name => name.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class ChunkedStream(byte[] bytes, int chunkSize) : MemoryStream(bytes)
    {
        public override int Read(byte[] buffer, int offset, int count)
            => base.Read(buffer, offset, Math.Min(count, chunkSize));
    }
}
