using System.IO.Pipes;

namespace WebSiteMonitor;

internal sealed class SingleInstanceCoordinator : IDisposable
{
    private const string MutexName = "Local\\WebSiteMonitor.App.Instance";
    private const string PipeName = "WebSiteMonitor.App.Command";
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();
    public bool IsPrimary { get; }
    public event Action? ShowRequested;

    public SingleInstanceCoordinator(string? isolatedProbeName = null)
    {
        var suffix = isolatedProbeName is null ? "" : ".probe." + isolatedProbeName;
        _pipeName = PipeName + suffix;
        _mutex = new Mutex(true, MutexName + suffix, out var created); IsPrimary = created;
        if (created) _ = ListenAsync();
    }

    public async Task NotifyPrimaryAsync()
    {
        try { using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out); await pipe.ConnectAsync(1500); using var writer = new StreamWriter(pipe) { AutoFlush = true }; await writer.WriteLineAsync("SHOW"); } catch { }
    }

    private async Task ListenAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_stop.Token);
                using var reader = new StreamReader(server); if (await reader.ReadLineAsync(_stop.Token) == "SHOW") ShowRequested?.Invoke();
            }
            catch (OperationCanceledException) { break; }
            catch { await Task.Delay(250); }
        }
    }

    public void Dispose() { _stop.Cancel(); if (IsPrimary) { try { _mutex.ReleaseMutex(); } catch { } } _mutex.Dispose(); _stop.Dispose(); }
}
