using Navigator.Core.Platform;

namespace Navigator.Core.IO;

/// <summary>
/// Shares one volume enumeration between everything that asks at about the same time (Home, the sidebar's Drives
/// section, the Network plugin). Querying drives can block on slow or disconnected network shares, so concurrent
/// callers wait for the single in-flight query instead of starting their own, and a result is reused for
/// <see cref="MaxAge"/>. A volume change event drops the snapshot immediately. The inner provider is subscribed to
/// only while someone listens here, so its change polling stays as lazy as before.
/// </summary>
public sealed class VolumeSnapshotCache : IVolumeProvider, IDisposable
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(2);

    private readonly IVolumeProvider _inner;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _query = new(1, 1);
    private readonly Func<long> _clock;
    private IReadOnlyList<VolumeInfo>? _snapshot;
    private long _takenAt;
    private long _generation;

    public VolumeSnapshotCache(IVolumeProvider inner)
        : this(inner, () => Environment.TickCount64)
    {
    }

    internal VolumeSnapshotCache(IVolumeProvider inner, Func<long> clock)
    {
        _inner = inner;
        _clock = clock;
    }

    private EventHandler? _changed;

    public event EventHandler? VolumesChanged
    {
        add
        {
            bool first;
            lock (_gate)
            {
                first = _changed is null;
                _changed += value;
            }

            if (first)
                _inner.VolumesChanged += OnInnerChanged;
        }

        remove
        {
            bool last;
            lock (_gate)
            {
                var had = _changed is not null;
                _changed -= value;
                last = had && _changed is null;
            }

            if (last)
                _inner.VolumesChanged -= OnInnerChanged;
        }
    }

    public IReadOnlyList<VolumeInfo> GetVolumes()
    {
        if (TryGetFresh(out var cached))
            return cached;

        _query.Wait();
        try
        {
            // Another caller may have refreshed the snapshot while this one waited.
            if (TryGetFresh(out cached))
                return cached;
            long generation;
            lock (_gate)
                generation = _generation;
            var volumes = _inner.GetVolumes();
            lock (_gate)
            {
                // A change event during the query makes this result stale: return it, but do not keep it.
                if (generation == _generation)
                {
                    _snapshot = volumes;
                    _takenAt = _clock();
                }
            }

            return volumes;
        }
        finally
        {
            _query.Release();
        }
    }

    private bool TryGetFresh(out IReadOnlyList<VolumeInfo> volumes)
    {
        lock (_gate)
        {
            if (_snapshot is not null && _clock() - _takenAt < (long)MaxAge.TotalMilliseconds)
            {
                volumes = _snapshot;
                return true;
            }
        }

        volumes = [];
        return false;
    }

    private void OnInnerChanged(object? sender, EventArgs e)
    {
        EventHandler? handlers;
        lock (_gate)
        {
            _snapshot = null;
            _generation++;
            handlers = _changed;
        }

        handlers?.Invoke(this, e);
    }

    public void Dispose()
    {
        _inner.VolumesChanged -= OnInnerChanged;
        _query.Dispose();
    }
}
