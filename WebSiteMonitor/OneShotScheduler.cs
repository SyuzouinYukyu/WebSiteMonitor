using System.Collections.Concurrent;
using WebSiteMonitor.Core;

namespace WebSiteMonitor;

internal sealed class OneShotScheduler : IDisposable
{
    private readonly Database _database;
    private readonly MonitorEngine _engine;
    private readonly SemaphoreSlim _parallel = new(4, 4);
    private readonly KeyedSiteGate _siteGate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly System.Threading.Timer _timer;
    private readonly ConcurrentDictionary<long, Task> _activeTasks = new();
    private readonly object _shutdownSync = new();
    private readonly object _activitySync = new();
    private Task? _stopTask;
    private long _taskSequence;
    private int _running;
    private int _stopping;
    private int _resourcesDisposed;
    private int _configurationSuspended;

    public bool Paused { get; private set; }
    public int RunningCount => Volatile.Read(ref _running);
    public event Action<CheckResult>? CheckCompleted;
    public event Action<Exception>? BackgroundError;

    public OneShotScheduler(Database database, MonitorEngine engine)
    {
        _database = database;
        _engine = engine;
        _database.SitesChanged += DatabaseSitesChanged;
        _timer = new System.Threading.Timer(_ => QueueDueRun(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start() => Arm();

    public void TogglePause()
    {
        if (Volatile.Read(ref _stopping) != 0) return;
        Paused = !Paused;
        Arm();
    }

    public void ScheduleChanged() => Arm();

    public async Task<IDisposable> SuspendForConfigurationAsync()
    {
        Task[] tasks;
        lock (_activitySync)
        {
            if (_configurationSuspended != 0 || _stopping != 0) throw new InvalidOperationException("設定処理を開始できません。");
            _configurationSuspended = 1;
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
            tasks = _activeTasks.Values.ToArray();
        }
        var lease = new ConfigurationLease(this);
        try { await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false); return lease; }
        catch { lease.Dispose(); throw; }
    }

    private sealed class ConfigurationLease(OneShotScheduler scheduler) : IDisposable
    {
        private OneShotScheduler? _scheduler = scheduler;
        public void Dispose()
        {
            var value = Interlocked.Exchange(ref _scheduler, null);
            if (value is null) return;
            Volatile.Write(ref value._configurationSuspended, 0);
            if (Volatile.Read(ref value._stopping) == 0) value.Arm();
        }
    }

    private void DatabaseSitesChanged(object? sender, EventArgs e)
    {
        if (Volatile.Read(ref _stopping) == 0) Arm();
    }

    public Task CheckNowAsync(IEnumerable<Site> sites)
    {
        if (Volatile.Read(ref _stopping) != 0) return Task.CompletedTask;
        var requested = sites.ToArray();
        return StartTracked(() => CheckManyAsync(requested));
    }

    private void QueueDueRun()
    {
        if (Paused || Volatile.Read(ref _stopping) != 0) return;
        _ = StartTracked(RunDueAsync);
    }

    private async Task RunDueAsync()
    {
        if (Paused || Volatile.Read(ref _stopping) != 0) return;
        try
        {
            var now = DateTimeOffset.Now;
            var due = _database.GetSites().Where(s => s.Enabled && s.ScheduleMode != ScheduleMode.Manual && (s.NextDue is null || s.NextDue <= now)).ToArray();
            await CheckManyAsync(due).ConfigureAwait(false);
        }
        finally
        {
            Arm();
        }
    }

    private async Task CheckManyAsync(IReadOnlyCollection<Site> sites)
    {
        if (sites.Count == 0) { Arm(); return; }
        await Task.WhenAll(sites.Select(CheckOneAsync)).ConfigureAwait(false);
        Arm();
    }

    private async Task CheckOneAsync(Site site)
    {
        if (Volatile.Read(ref _stopping) != 0) return;
        using var siteLease = await _siteGate.AcquireAsync(site.Id, _stop.Token).ConfigureAwait(false);
        if (Volatile.Read(ref _stopping) != 0) return;
        await _parallel.WaitAsync(_stop.Token).ConfigureAwait(false);
        Interlocked.Increment(ref _running);
        try
        {
            var result = await _engine.CheckAsync(site, _stop.Token).ConfigureAwait(false);
            CheckCompleted?.Invoke(result);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        finally
        {
            Interlocked.Decrement(ref _running);
            _parallel.Release();
        }
    }

    private Task StartTracked(Func<Task> start)
    {
        Task task;
        long id;
        lock (_activitySync)
        {
            if (Volatile.Read(ref _stopping) != 0 || Volatile.Read(ref _configurationSuspended) != 0) return Task.CompletedTask;
            try { task = start(); }
            catch (Exception ex) { task = Task.FromException(ex); }
            id = Interlocked.Increment(ref _taskSequence);
            _activeTasks.TryAdd(id, task);
        }
        _ = ObserveAsync(id, task);
        return task;
    }

    private async Task ObserveAsync(long id, Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            BackgroundError?.Invoke(ex);
        }
        finally
        {
            _activeTasks.TryRemove(id, out _);
        }
    }

    private void Arm()
    {
        if (Paused || Volatile.Read(ref _stopping) != 0 || Volatile.Read(ref _configurationSuspended) != 0) { _timer.Change(Timeout.Infinite, Timeout.Infinite); return; }
        try
        {
            var next = _database.GetSites().Where(s => s.Enabled && s.ScheduleMode != ScheduleMode.Manual).Select(s => s.NextDue ?? DateTimeOffset.Now).DefaultIfEmpty(DateTimeOffset.Now.AddHours(24)).Min();
            var delay = next - DateTimeOffset.Now;
            if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
            if (delay > TimeSpan.FromDays(7)) delay = TimeSpan.FromDays(7);
            _timer.Change(delay, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref _stopping) != 0)
        {
        }
    }

    public Task StopAsync(TimeSpan? timeout = null)
    {
        lock (_shutdownSync)
        {
            return _stopTask ??= StopCoreAsync(timeout ?? TimeSpan.FromSeconds(10));
        }
    }

    private async Task StopCoreAsync(TimeSpan timeout)
    {
        Task[] tasks;
        lock (_activitySync)
        {
            if (Interlocked.Exchange(ref _stopping, 1) != 0) return;
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
            _timer.Dispose();
            _stop.Cancel();
            tasks = _activeTasks.Values.ToArray();
        }
        var all = tasks.Length == 0 ? Task.CompletedTask : Task.WhenAll(tasks);
        var completed = await Task.WhenAny(all, Task.Delay(timeout)).ConfigureAwait(false);
        if (completed == all)
        {
            try { await all.ConfigureAwait(false); } catch (OperationCanceledException) { } catch (Exception ex) { BackgroundError?.Invoke(ex); }
            DisposeResources();
            return;
        }
        _ = FinishAfterTasksAsync(all);
    }

    private async Task FinishAfterTasksAsync(Task all)
    {
        try { await all.ConfigureAwait(false); } catch { }
        DisposeResources();
    }

    private void DisposeResources()
    {
        if (Interlocked.Exchange(ref _resourcesDisposed, 1) != 0) return;
        _database.SitesChanged -= DatabaseSitesChanged;
        _parallel.Dispose();
        _stop.Dispose();
    }

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
    }
}
