using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using NAudio.Wave;
using WebSiteMonitor.Core;

namespace WebSiteMonitor;

internal enum SoundPlaybackState { Queued, Started, Completed, Failed, Cancelled }
internal enum SoundRequestKind { Notification, Test }

internal sealed record SoundPlaybackResult(SoundPlaybackState State, string Message, Exception? Exception = null, SoundRequestKind Kind = SoundRequestKind.Notification)
{
    public bool IsFailure => State == SoundPlaybackState.Failed;
    public bool IsFinal => State is SoundPlaybackState.Completed or SoundPlaybackState.Failed or SoundPlaybackState.Cancelled;
}

internal sealed class SoundRequest
{
    private CancellationTokenRegistration _cancellationRegistration;

    public SoundRequest(string path, int volume, AppSettings settings, SoundRequestKind kind, CancellationToken cancellationToken)
    {
        Path = path;
        Volume = Math.Clamp(volume, 0, 100);
        Settings = settings;
        Kind = kind;
        CancellationToken = cancellationToken;
        Completion = new TaskCompletionSource<SoundPlaybackResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Id = Guid.NewGuid();
    }

    public Guid Id { get; }
    public string Path { get; }
    public int Volume { get; }
    public AppSettings Settings { get; }
    public SoundRequestKind Kind { get; }
    public CancellationToken CancellationToken { get; }
    internal TaskCompletionSource<SoundPlaybackResult> Completion { get; }
    public Task<SoundPlaybackResult> CompletionTask => Completion.Task;
    internal bool IsCompleted => Completion.Task.IsCompleted;

    internal void RegisterCancellation(CancellationTokenRegistration registration)
    {
        if (Completion.Task.IsCompleted) registration.Dispose();
        else _cancellationRegistration = registration;
    }
    internal void DisposeRegistration() => _cancellationRegistration.Dispose();
}

internal sealed class SoundService : IDisposable
{
    public const int QueueCapacity = 256;
    public static readonly string[] Extensions = [".mp3", ".mp2", ".mp1", ".aac", ".m4a", ".m4b", ".m4p", ".alac", ".flac", ".wav", ".wave", ".wma", ".ogg", ".oga", ".opus", ".aiff", ".aif", ".aifc", ".amr", ".ac3", ".eac3", ".dts", ".dtshd", ".ape", ".wv", ".tta", ".tak", ".ra", ".ram", ".caf", ".au", ".snd", ".pcm", ".mid", ".midi", ".rmi"];
    public static string FileDialogFilter => "対応する音声ファイル|" + string.Join(';', Extensions.Select(x => "*" + x)) + "|すべてのファイル|*.*";

    private sealed class ActivePlayback : IDisposable
    {
        public ActivePlayback(SoundRequest request, CancellationTokenSource cancellation, bool isMidi)
        {
            Request = request;
            Cancellation = cancellation;
            IsMidi = isMidi;
        }

        public SoundRequest Request { get; }
        public CancellationTokenSource Cancellation { get; }
        public bool IsMidi { get; }
        public void Dispose() => Cancellation.Dispose();
    }

    private readonly object _sync = new();
    private readonly Channel<SoundRequest> _queue;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;
    private readonly Func<string, (int Code, string Value)> _mci;
    private readonly Func<SoundRequest, CancellationToken, Task<SoundPlaybackResult>>? _simulatedPlayback;
    private readonly FileLogger? _logger;
    private readonly HashSet<SoundRequest> _outstanding = [];
    private ActivePlayback? _active;
    private Task? _shutdownTask;
    private bool _accepting = true;
    private bool _disposed;
    private int _queuedCount;

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern int mciSendString(string command, StringBuilder? result, int resultLength, IntPtr callback);

    public event Action<SoundPlaybackResult>? PlaybackStateChanged;

    public SoundService() : this(null, null, QueueCapacity, null) { }
    internal SoundService(FileLogger logger) : this(null, null, QueueCapacity, logger) { }
    internal SoundService(Func<string, (int Code, string Value)> mci) : this(mci, null, QueueCapacity, null) { }
    internal SoundService(Func<SoundRequest, CancellationToken, Task<SoundPlaybackResult>> simulatedPlayback, int capacity = QueueCapacity) : this(null, simulatedPlayback, capacity, null) { }

    private SoundService(Func<string, (int Code, string Value)>? mci, Func<SoundRequest, CancellationToken, Task<SoundPlaybackResult>>? simulatedPlayback, int capacity, FileLogger? logger)
    {
        _mci = mci ?? NativeMci;
        _simulatedPlayback = simulatedPlayback;
        _logger = logger;
        _queue = Channel.CreateBounded<SoundRequest>(new BoundedChannelOptions(Math.Clamp(capacity, 1, QueueCapacity))
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        _worker = Task.Run(ConsumeAsync);
    }

    public static bool IsSupportedExtension(string path) => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    internal int ActivePlaybackCount { get { lock (_sync) return _active is null ? 0 : 1; } }
    internal int ActiveMidiCount { get { lock (_sync) return _active?.IsMidi == true ? 1 : 0; } }
    internal int PendingCount => Math.Max(0, Volatile.Read(ref _queuedCount));

    public Task<SoundPlaybackResult> PlayAsync(string path, int volume, AppSettings settings, CancellationToken cancellationToken = default)
        => Enqueue(path, volume, settings, SoundRequestKind.Notification, cancellationToken);

    internal Task<SoundPlaybackResult> PlayTestAsync(string path, int volume, AppSettings settings, CancellationToken cancellationToken = default)
        => Enqueue(path, volume, settings, SoundRequestKind.Test, cancellationToken);

    private Task<SoundPlaybackResult> Enqueue(string path, int volume, AppSettings settings, SoundRequestKind kind, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return Finished(kind, SoundPlaybackState.Cancelled, "通知音の再生を停止しました。");
        var request = new SoundRequest(path, volume, settings, kind, cancellationToken);
        lock (_sync)
        {
            if (!_accepting || _disposed)
            {
                Complete(request, new SoundPlaybackResult(SoundPlaybackState.Cancelled, "アプリケーション終了中のため通知音を再生しません。", null, kind));
                return request.CompletionTask;
            }
            if (!_queue.Writer.TryWrite(request))
            {
                var result = new SoundPlaybackResult(SoundPlaybackState.Failed, "通知音再生キューが満杯のため、この通知音を再生できませんでした。", null, kind);
                _logger?.Error("通知音再生キューが満杯です。通知音を破棄しました。");
                Complete(request, result);
                return request.CompletionTask;
            }
            _outstanding.Add(request);
            Interlocked.Increment(ref _queuedCount);
        }
        if (cancellationToken.CanBeCanceled)
            request.RegisterCancellation(cancellationToken.Register(() => CancelRequest(request, "通知音の再生を停止しました。")));
        Raise(new SoundPlaybackResult(SoundPlaybackState.Queued, "通知音の再生待機中です。", null, kind));
        return request.CompletionTask;
    }

    /// <summary>Cancels only an active test request; monitoring notifications remain in FIFO order.</summary>
    public bool StopTestPlayback()
    {
        ActivePlayback? active;
        lock (_sync) active = _active?.Request.Kind == SoundRequestKind.Test ? _active : null;
        if (active is null) return false;
        try { active.Cancellation.Cancel(); } catch (ObjectDisposedException) { return false; }
        return true;
    }

    // Compatibility alias: it intentionally never stops notification requests.
    public void Stop() => StopTestPlayback();

    private async Task ConsumeAsync()
    {
        try
        {
            await foreach (var request in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                Interlocked.Decrement(ref _queuedCount);
                if (request.IsCompleted) continue;
                try { await ExecuteAsync(request).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    _logger?.Error("通知音キューの要求処理に失敗しました。", ex);
                    Complete(request, Failure(request.Kind, ex));
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.Error("通知音キュー worker が停止しました。", ex);
        }
        finally
        {
            DrainPending();
        }
    }

    private async Task ExecuteAsync(SoundRequest request)
    {
        if (request.IsCompleted) return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, request.CancellationToken);
        using var active = new ActivePlayback(request, cancellation, IsMidi(request.Path));
        lock (_sync)
        {
            if (request.IsCompleted) return;
            _active = active;
        }
        SoundPlaybackResult result;
        try
        {
            result = await PlayOneAsync(request, cancellation.Token).ConfigureAwait(false);
            if (cancellation.IsCancellationRequested && result.State != SoundPlaybackState.Failed)
                result = new SoundPlaybackResult(SoundPlaybackState.Cancelled, "通知音の再生を停止しました。", null, request.Kind);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            result = new SoundPlaybackResult(SoundPlaybackState.Cancelled, "通知音の再生を停止しました。", null, request.Kind);
        }
        catch (Exception ex)
        {
            result = Failure(request.Kind, ex);
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_active, active)) _active = null;
            }
        }
        Complete(request, result with { Kind = request.Kind });
    }

    private async Task<SoundPlaybackResult> PlayOneAsync(SoundRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(request.Path)) throw new FileNotFoundException("通知音ファイルが見つかりません。", request.Path);
        if (!IsSupportedExtension(request.Path)) throw new NotSupportedException("対応する音声拡張子ではありません。");
        if (_simulatedPlayback is not null)
        {
            Raise(new SoundPlaybackResult(SoundPlaybackState.Started, "通知音の再生を開始しました。", null, request.Kind));
            return await _simulatedPlayback(request, cancellationToken).ConfigureAwait(false);
        }
        return IsMidi(request.Path)
            ? await PlayMidiAsync(request, cancellationToken).ConfigureAwait(false)
            : await PlayWaveAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SoundPlaybackResult> PlayWaveAsync(SoundRequest request, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(request.Path).ToLowerInvariant();
        var input = extension == ".ram" ? ResolveRamReference(request.Path) : request.Path;
        WaveStream? reader = null;
        IWavePlayer? output = null;
        EventHandler<StoppedEventArgs>? stopped = null;
        try
        {
            reader = extension == ".pcm"
                ? new RawSourceWaveStream(File.OpenRead(input), new WaveFormat(request.Settings.PcmSampleRate, request.Settings.PcmBits, request.Settings.PcmChannels))
                : new FfmpegPcmWaveStream(input);
            output = new WaveOutEvent { Volume = request.Volume / 100f };
            var completion = new TaskCompletionSource<SoundPlaybackResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            stopped = (_, args) => completion.TrySetResult(PlaybackStoppedResult(args.Exception) with { Kind = request.Kind });
            output.PlaybackStopped += stopped;
            using var registration = cancellationToken.Register(() => completion.TrySetResult(new SoundPlaybackResult(SoundPlaybackState.Cancelled, "通知音の再生を停止しました。", null, request.Kind)));
            output.Init(reader);
            output.Play();
            Raise(new SoundPlaybackResult(SoundPlaybackState.Started, "通知音の再生を開始しました。", null, request.Kind));
            return await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            if (output is not null)
            {
                if (stopped is not null) output.PlaybackStopped -= stopped;
                try { output.Stop(); } catch { }
                output.Dispose();
            }
            reader?.Dispose();
        }
    }

    private async Task<SoundPlaybackResult> PlayMidiAsync(SoundRequest request, CancellationToken cancellationToken)
    {
        var alias = "WebSiteMonitorMidi" + Guid.NewGuid().ToString("N");
        var quoted = request.Path.Replace("\"", "\"\"");
        var opened = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var open = SendMci($"open \"{quoted}\" type sequencer alias {alias}").Code;
            if (open != 0) throw new InvalidOperationException($"MIDIを開けません (MCI {open})");
            opened = true;
            var setVolume = SendMci($"setaudio {alias} volume to {request.Volume * 10}").Code;
            if (setVolume != 0) throw new InvalidOperationException($"MIDI音量を設定できません (MCI {setVolume})");
            var play = SendMci($"play {alias}").Code;
            if (play != 0) throw new InvalidOperationException($"MIDIを再生できません (MCI {play})");
            Raise(new SoundPlaybackResult(SoundPlaybackState.Started, "MIDI通知音の再生を開始しました。", null, request.Kind));
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var status = SendMci($"status {alias} mode");
                if (status.Code != 0) throw new InvalidOperationException($"MIDI再生状態を取得できません (MCI {status.Code})");
                if (!string.Equals(status.Value.Trim(), "playing", StringComparison.OrdinalIgnoreCase))
                    return new SoundPlaybackResult(SoundPlaybackState.Completed, "MIDI通知音の再生が完了しました。", null, request.Kind);
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (opened) CloseMidiDevice(alias, cancellationToken.IsCancellationRequested);
        }
    }

    internal static SoundPlaybackResult PlaybackStoppedResult(Exception? exception) => exception is null
        ? new SoundPlaybackResult(SoundPlaybackState.Completed, "通知音の再生が完了しました。")
        : new SoundPlaybackResult(SoundPlaybackState.Failed, DescribeFailure(exception), exception);

    private void CancelRequest(SoundRequest request, string message)
    {
        ActivePlayback? active;
        lock (_sync) active = ReferenceEquals(_active?.Request, request) ? _active : null;
        if (active is null)
        {
            Complete(request, new SoundPlaybackResult(SoundPlaybackState.Cancelled, message, null, request.Kind));
            return;
        }
        try { active.Cancellation.Cancel(); } catch (ObjectDisposedException) { }
    }

    private static bool IsMidi(string path) => Path.GetExtension(path) is ".mid" or ".midi" or ".rmi";

    private void DrainPending()
    {
        while (_queue.Reader.TryRead(out var request))
        {
            Interlocked.Decrement(ref _queuedCount);
            Complete(request, new SoundPlaybackResult(SoundPlaybackState.Cancelled, "アプリケーション終了のため通知音を再生しません。", null, request.Kind));
        }
    }

    private Task<SoundPlaybackResult> Finished(SoundRequestKind kind, SoundPlaybackState state, string message)
    {
        var result = new SoundPlaybackResult(state, message, null, kind);
        Raise(result);
        return Task.FromResult(result);
    }

    private void Complete(SoundRequest request, SoundPlaybackResult result)
    {
        if (request.Completion.TrySetResult(result))
        {
            lock (_sync) _outstanding.Remove(request);
            request.DisposeRegistration();
            Raise(result);
        }
    }

    private static SoundPlaybackResult Failure(SoundRequestKind kind, Exception exception)
        => new(SoundPlaybackState.Failed, DescribeFailure(exception), exception, kind);

    private void Raise(SoundPlaybackResult result)
    {
        var handlers = PlaybackStateChanged;
        if (handlers is null) return;
        foreach (Action<SoundPlaybackResult> handler in handlers.GetInvocationList())
        {
            try { handler(result); } catch { }
        }
    }

    private void CloseMidiDevice(string alias, bool stop)
    {
        try { if (stop) SendMci($"stop {alias}"); } catch { }
        try { SendMci($"close {alias}"); } catch { }
    }

    private (int Code, string Value) SendMci(string command) => _mci(command);

    private static (int Code, string Value) NativeMci(string command)
    {
        var result = new StringBuilder(64);
        var code = mciSendString(command, result, result.Capacity, IntPtr.Zero);
        return (code, result.ToString());
    }

    private static string ResolveRamReference(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > 256 * 1024) throw new InvalidDataException("RAM参照ファイルが大きすぎます。");
        using var reader = new StreamReader(path, detectEncodingFromByteOrderMarks: true);
        while (reader.ReadLine() is { } line)
        {
            var value = line.Trim();
            if (value.Length == 0 || value.StartsWith('#') || value.StartsWith(';')) continue;
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)) return uri.AbsoluteUri;
            throw new InvalidDataException("RAM参照には http または https の音声URLだけを指定できます。");
        }
        throw new InvalidDataException("RAM参照に再生可能なURLがありません。");
    }

    private static string DescribeFailure(Exception exception)
    {
        var message = exception.Message.Trim();
        if (message.Contains("M4P", StringComparison.OrdinalIgnoreCase) || message.Contains("DRM", StringComparison.OrdinalIgnoreCase))
            return "通知音を再生できません。M4Pの場合はDRM保護されている可能性があります。DRMの解除・回避は行いません。";
        return "通知音を再生できません: " + message;
    }

    public Task ShutdownAsync(TimeSpan? timeout = null)
    {
        lock (_sync) return _shutdownTask ??= ShutdownCoreAsync(timeout ?? TimeSpan.FromSeconds(3));
    }

    private async Task ShutdownCoreAsync(TimeSpan timeout)
    {
        ActivePlayback? active;
        List<SoundRequest> pending;
        lock (_sync)
        {
            _accepting = false;
            _queue.Writer.TryComplete();
            active = _active;
            pending = _outstanding.Where(request => !ReferenceEquals(active?.Request, request)).ToList();
        }
        foreach (var request in pending)
            Complete(request, new SoundPlaybackResult(SoundPlaybackState.Cancelled, "アプリケーション終了のため通知音を再生しません。", null, request.Kind));
        if (active is not null)
        {
            try { active.Cancellation.Cancel(); } catch (ObjectDisposedException) { }
        }
        var finished = await Task.WhenAny(_worker, Task.Delay(timeout)).ConfigureAwait(false);
        if (!ReferenceEquals(finished, _worker))
        {
            _logger?.Error("通知音キュー worker の終了待機がタイムアウトしました。");
            if (active is not null)
                Complete(active.Request, new SoundPlaybackResult(SoundPlaybackState.Cancelled, "アプリケーション終了のため通知音を停止しました。", null, active.Request.Kind));
            return;
        }
        await _worker.ConfigureAwait(false);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
        }
        try { ShutdownAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult(); }
        finally { _shutdown.Dispose(); }
    }
}
internal static class FfmpegBackend
{
    public const string MissingMessage = "FFmpegが見つかりません。FFmpegをインストールし、ffmpeg.exeのあるフォルダーをPATHへ登録してください。";

    // The override is for deterministic tests; production always reads the current process PATH.
    public static string ResolveExecutable(string? searchPath = null)
    {
        var path = searchPath ?? Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var entry in path.Split(';'))
        {
            var directory = entry.Trim();
            if (directory.Length >= 2 && directory[0] == '"' && directory[^1] == '"')
                directory = directory[1..^1];
            if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory)) continue;
            try
            {
                var executable = Path.GetFullPath(Path.Combine(directory, "ffmpeg.exe"));
                if (File.Exists(executable)) return executable;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
            {
                // Ignore malformed/inaccessible PATH entries; do not fall back to the working directory.
            }
        }
        throw new FileNotFoundException(MissingMessage);
    }
}

internal sealed class FfmpegPcmWaveStream : WaveStream
{
    private readonly Process _process;
    private readonly Stream _stdout;
    private readonly Task<string> _stderr;
    private readonly WaveFormat _format = new(44100, 16, 2);
    private bool _disposed;

    public FfmpegPcmWaveStream(string input, string? executablePath = null)
    {
        if (executablePath is not null && !Path.IsPathFullyQualified(executablePath))
            throw new ArgumentException("FFmpegの実行パスには絶対パスが必要です。", nameof(executablePath));
        var executable = executablePath is null ? FfmpegBackend.ResolveExecutable() : Path.GetFullPath(executablePath);
        var start = CreateStartInfo(executable, input);
        _process = Process.Start(start) ?? throw new InvalidOperationException("FFmpegを起動できません。");
        _stdout = _process.StandardOutput.BaseStream;
        _stderr = _process.StandardError.ReadToEndAsync();
    }

    internal static ProcessStartInfo CreateStartInfo(string executable, string input)
    {
        var start = new ProcessStartInfo
        {
            FileName = executable, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
        };
        start.ArgumentList.Add("-nostdin");
        start.ArgumentList.Add("-v");
        start.ArgumentList.Add("error");
        start.ArgumentList.Add("-i");
        start.ArgumentList.Add(input);
        start.ArgumentList.Add("-vn");
        start.ArgumentList.Add("-f");
        start.ArgumentList.Add("s16le");
        start.ArgumentList.Add("-acodec");
        start.ArgumentList.Add("pcm_s16le");
        start.ArgumentList.Add("-ac");
        start.ArgumentList.Add("2");
        start.ArgumentList.Add("-ar");
        start.ArgumentList.Add("44100");
        start.ArgumentList.Add("pipe:1");
        return start;
    }

    public override WaveFormat WaveFormat => _format;
    internal int ProcessId => _process.Id;
    public override long Length => 0;
    public override long Position { get => 0; set => throw new NotSupportedException(); }
    public override bool CanRead => !_disposed;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (count == 0) return 0;
        try
        {
            var read = FillPcmBuffer(_stdout, buffer, offset, count);
            if (read != 0) return read;
            if (!_process.WaitForExit(1500)) throw new InvalidDataException("FFmpegが終了せず音声ストリームを閉じられません。");
            var stderr = _stderr.GetAwaiter().GetResult().Trim();
            if (_process.HasExited && (_process.ExitCode != 0 || stderr.Length != 0))
                throw new InvalidDataException(string.IsNullOrWhiteSpace(stderr) ? "FFmpegで音声をデコードできません。M4Pの場合はDRM保護されている可能性があります。" : stderr);
            return 0;
        }
        catch (InvalidDataException) { throw; }
        catch (Exception ex) when (!_disposed) { throw new InvalidDataException("FFmpeg音声ストリームの読み取りに失敗しました。", ex); }
    }

    internal static int FillPcmBuffer(Stream source, byte[] buffer, int offset, int count)
    {
        var total = 0;
        while (total < count)
        {
            var read = source.Read(buffer, offset + total, count - total);
            if (read == 0) break;
            total += read;
        }
        return total;
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;
        if (disposing)
        {
            try { _stdout.Dispose(); } catch { }
            try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { }
            try { _process.WaitForExit(1500); } catch { }
            _process.Dispose();
        }
        base.Dispose(disposing);
    }
}
