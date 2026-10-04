using System.Diagnostics;

namespace WebSiteMonitor.Tests;

public sealed class FfmpegTestRuntime
{
    public string Executable => global::WebSiteMonitor.FfmpegBackend.ResolveExecutable();
}

public sealed class ExternalFfmpegFactAttribute : FactAttribute
{
    public ExternalFfmpegFactAttribute()
    {
        try { global::WebSiteMonitor.FfmpegBackend.ResolveExecutable(); }
        catch (FileNotFoundException) { Skip = "PATHにFFmpegがないため、実音声結合テストは未実施です。"; }
    }
}

public sealed class ExternalFfmpegTheoryAttribute : TheoryAttribute
{
    public ExternalFfmpegTheoryAttribute()
    {
        try { global::WebSiteMonitor.FfmpegBackend.ResolveExecutable(); }
        catch (FileNotFoundException) { Skip = "PATHにFFmpegがないため、実音声結合テストは未実施です。"; }
    }
}

public sealed class FfmpegDecoderIntegrationTests : IClassFixture<FfmpegTestRuntime>, IDisposable
{
    private readonly FfmpegTestRuntime _runtime;
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "WebSiteMonitorFfmpegTests-" + Guid.NewGuid().ToString("N"));

    public FfmpegDecoderIntegrationTests(FfmpegTestRuntime runtime)
    {
        _runtime = runtime;
        Directory.CreateDirectory(_temp);
    }

    [ExternalFfmpegTheory]
    [InlineData("mp3", "libmp3lame")]
    [InlineData("aac", "aac")]
    [InlineData("m4a", "aac")]
    [InlineData("flac", "flac")]
    [InlineData("wav", "pcm_s16le")]
    [InlineData("ogg", "libvorbis")]
    [InlineData("opus", "libopus")]
    [InlineData("wma", "wmav2")]
    [InlineData("aiff", "pcm_s16be")]
    [InlineData("ac3", "ac3")]
    [InlineData("wv", "wavpack")]
    public void ExternalFfmpegDecodesRepresentativeAudioUsingProductRuntime(string extension, string codec)
    {
        var input = Path.Combine(_temp, "tone." + extension);
        GenerateTone(_runtime.Executable, input, codec);
        using var stream = new global::WebSiteMonitor.FfmpegPcmWaveStream(input, _runtime.Executable);
        var buffer = new byte[4096];
        Assert.True(stream.Read(buffer, 0, buffer.Length) > 0, $"External FFmpeg did not produce PCM for .{extension}.");
    }

    [ExternalFfmpegFact]
    public void ApeDecoderCapabilityIsCheckedWithoutTreatingMuxerAbsenceAsFailure()
    {
        var start = new ProcessStartInfo { FileName = _runtime.Executable, UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
        start.ArgumentList.Add("-hide_banner");
        start.ArgumentList.Add("-decoders");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("External FFmpeg did not start.");
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        Assert.Contains("ape", output, StringComparison.OrdinalIgnoreCase);
    }

    [ExternalFfmpegFact]
    public void InvalidAudioReportsDecoderFailure()
    {
        var input = Path.Combine(_temp, "invalid.mp3");
        File.WriteAllText(input, "not audio");
        using var stream = new global::WebSiteMonitor.FfmpegPcmWaveStream(input, _runtime.Executable);
        var buffer = new byte[4096];
        Assert.Throws<InvalidDataException>(() => stream.Read(buffer, 0, buffer.Length));
    }

    private static void GenerateTone(string executable, string output, string codec)
    {
        var start = new ProcessStartInfo { FileName = executable, UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add("-nostdin");
        start.ArgumentList.Add("-y");
        start.ArgumentList.Add("-loglevel");
        start.ArgumentList.Add("error");
        start.ArgumentList.Add("-f");
        start.ArgumentList.Add("lavfi");
        start.ArgumentList.Add("-i");
        start.ArgumentList.Add("sine=frequency=440:sample_rate=44100");
        start.ArgumentList.Add("-t");
        start.ArgumentList.Add("0.25");
        start.ArgumentList.Add("-c:a");
        start.ArgumentList.Add(codec);
        start.ArgumentList.Add(output);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("External FFmpeg did not start.");
        var standardError = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(10000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("外部FFmpegによる音声テストソース生成が10秒以内に完了しませんでした。");
        }
        if (process.ExitCode != 0) throw new InvalidOperationException("音声テストソースを生成できません: " + standardError);
    }

    [ExternalFfmpegFact]
    public void DisposingDecoderStopsTheActualExternalProcess()
    {
        var input = Path.Combine(_temp, "long.pcm");
        using (var file = File.Create(input)) file.SetLength(44100 * 2 * 2 * 120);
        // FFmpeg blocks on its redirected pipe while nobody reads this long source.
        var wav = Path.Combine(_temp, "long.wav");
        var start = new ProcessStartInfo { FileName = _runtime.Executable, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "-nostdin", "-v", "error", "-f", "s16le", "-ar", "44100", "-ac", "2", "-i", input, wav }) start.ArgumentList.Add(arg);
        using (var generation = Process.Start(start)!) Assert.True(generation.WaitForExit(10000));
        var decoder = new global::WebSiteMonitor.FfmpegPcmWaveStream(wav, _runtime.Executable);
        using var process = Process.GetProcessById(decoder.ProcessId);
        Assert.False(process.HasExited); decoder.Dispose();
        Assert.True(process.WaitForExit(3000));
    }

    public void Dispose()
    {
        try { Directory.Delete(_temp, true); } catch { }
    }
}
