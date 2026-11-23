using NaSeqMen.Nodes.Interception;

namespace NaSeqMen.Tests;

internal sealed class MockInterceptionBackend : IInterceptionBackend
{
    private readonly object _sync = new();
    private readonly Dictionary<int, string> _hardwareIds = new();
    private readonly ushort[] _slotFilters = new ushort[InterceptionConstants.MaxDevice + 1];
    private readonly Queue<(int Device, InterceptionStroke Stroke, TaskCompletionSource Done)> _incoming = new();
    private readonly List<(int Device, InterceptionStroke Stroke)> _sent = new();
    private TaskCompletionSource? _inFlightDone;
    private long _nextContext = 1;
    private int _filterApplyCount;

    public bool IsAvailable => true;
    public string? LoadedLibraryPath => "mock://interception";
    public string? LastError => null;

    public void SetHardwareId(int slot, string hwid)
    {
        lock (_sync)
        {
            if (string.IsNullOrWhiteSpace(hwid))
                _hardwareIds.Remove(slot);
            else
                _hardwareIds[slot] = hwid;
        }
    }

    public ushort GetSlotFilter(int slot)
    {
        lock (_sync) return _slotFilters[slot];
    }

    public ushort[] SnapshotFilters()
    {
        lock (_sync) return (ushort[])_slotFilters.Clone();
    }

    public int FilterApplyCount
    {
        get { lock (_sync) return _filterApplyCount; }
    }

    public IReadOnlyList<(int Device, InterceptionStroke Stroke)> SnapshotSent()
    {
        lock (_sync) return _sent.ToArray();
    }

    public void ClearSent()
    {
        lock (_sync) _sent.Clear();
    }

    public async Task InjectAndWaitAsync(int device, InterceptionStroke stroke, int timeoutMs = 3000)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            _incoming.Enqueue((device, stroke, done));
            Monitor.PulseAll(_sync);
        }

        await done.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
    }

    public nint CreateContext() => (nint)Interlocked.Increment(ref _nextContext);

    public void DestroyContext(nint context)
    {
        lock (_sync)
        {
            _inFlightDone?.TrySetResult();
            _inFlightDone = null;
        }
    }

    public void SetFilter(nint context, InterceptionPredicate predicate, ushort filter)
    {
        lock (_sync)
        {
            for (var slot = 1; slot <= InterceptionConstants.MaxDevice; slot++)
            {
                if (predicate(slot) != 0)
                    _slotFilters[slot] = filter;
            }
            _filterApplyCount++;
        }
    }

    public int WaitWithTimeout(nint context, uint milliseconds)
    {
        lock (_sync)
        {
            if (_incoming.Count == 0)
                Monitor.Wait(_sync, (int)Math.Clamp(milliseconds, 1, 250));

            return _incoming.Count > 0 ? _incoming.Peek().Device : 0;
        }
    }

    public int Receive(nint context, int device, ref InterceptionStroke stroke)
    {
        lock (_sync)
        {
            if (_inFlightDone is not null)
            {
                _inFlightDone.TrySetResult();
                _inFlightDone = null;
            }

            if (_incoming.Count > 0 && _incoming.Peek().Device == device)
            {
                var item = _incoming.Dequeue();
                _inFlightDone = item.Done;
                stroke = item.Stroke;
                return 1;
            }

            return 0;
        }
    }

    public int Send(nint context, int device, in InterceptionStroke stroke)
    {
        lock (_sync)
        {
            _sent.Add((device, stroke));
            return 1;
        }
    }

    public string GetHardwareId(nint context, int device)
    {
        lock (_sync)
            return _hardwareIds.TryGetValue(device, out var hwid) ? hwid : string.Empty;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _inFlightDone?.TrySetResult();
            _inFlightDone = null;
            while (_incoming.Count > 0)
                _incoming.Dequeue().Done.TrySetResult();
            Monitor.PulseAll(_sync);
        }
    }
}
