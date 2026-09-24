using System.Collections.Concurrent;

namespace WebSiteMonitor.Core;

/// <summary>Serializes work for a site while allowing unrelated site IDs to run in parallel.</summary>
public sealed class KeyedSiteGate
{
    private sealed class Entry
    {
        public readonly object Sync = new();
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public int Users;
        public bool Retired;
    }

    private readonly ConcurrentDictionary<long, Entry> _entries = new();
    public int ActiveKeyCount => _entries.Count;

    public async ValueTask<IDisposable> AcquireAsync(long siteId, CancellationToken cancellationToken)
    {
        Entry entry;
        while (true)
        {
            entry = _entries.GetOrAdd(siteId, static _ => new Entry());
            lock (entry.Sync)
            {
                if (!entry.Retired)
                {
                    entry.Users++;
                    break;
                }
            }
        }

        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new Lease(this, siteId, entry);
        }
        catch
        {
            ReleaseUser(siteId, entry);
            throw;
        }
    }

    private void Release(long siteId, Entry entry)
    {
        entry.Semaphore.Release();
        ReleaseUser(siteId, entry);
    }

    private void ReleaseUser(long siteId, Entry entry)
    {
        lock (entry.Sync)
        {
            entry.Users--;
            if (entry.Users != 0) return;
            entry.Retired = true;
            _entries.TryRemove(new KeyValuePair<long, Entry>(siteId, entry));
        }
    }

    private sealed class Lease : IDisposable
    {
        private KeyedSiteGate? _owner;
        private readonly long _siteId;
        private readonly Entry _entry;

        public Lease(KeyedSiteGate owner, long siteId, Entry entry)
        {
            _owner = owner;
            _siteId = siteId;
            _entry = entry;
        }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?.Release(_siteId, _entry);
        }
    }
}