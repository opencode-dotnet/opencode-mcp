namespace OpenCode.Mcp.Tools.Tests.Native.Support;

internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly Lock _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
    private long _sequence;

    public int ArmedTimerCount
    {
        get
        {
            lock (_gate)
            {
                return _timers.Count(static timer => timer.DueAt is not null);
            }
        }
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => GetUtcNow().UtcTicks;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (_gate)
        {
            timer.Change(dueTime, period);
            _timers.Add(timer);
        }
        return timer;
    }

    public int Advance(TimeSpan amount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(amount, TimeSpan.Zero);
        DateTimeOffset target;
        lock (_gate)
        {
            target = _now + amount;
        }
        var fired = 0;
        while (true)
        {
            ManualTimer? timer;
            lock (_gate)
            {
                timer = _timers.Where(candidate => candidate.DueAt <= target)
                    .OrderBy(static timer => timer.DueAt).ThenBy(static timer => timer.Order).FirstOrDefault();
                if (timer is null)
                {
                    _now = target;
                    return fired;
                }
                _now = timer.DueAt.GetValueOrDefault();
                timer.DueAt = timer.Period > TimeSpan.Zero ? _now + timer.Period : null;
                timer.Order = _sequence++;
            }
            fired++;
            timer.Callback(timer.State);
        }
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private bool _disposed;
        public TimerCallback Callback { get; } = callback;
        public object? State { get; } = state;
        public DateTimeOffset? DueAt { get; set; }
        public TimeSpan Period { get; set; }
        public long Order { get; set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime < TimeSpan.Zero && dueTime != Timeout.InfiniteTimeSpan)
            {
                throw new ArgumentOutOfRangeException(nameof(dueTime));
            }
            if (period < TimeSpan.Zero && period != Timeout.InfiniteTimeSpan)
            {
                throw new ArgumentOutOfRangeException(nameof(period));
            }
            lock (owner._gate)
            {
                if (_disposed)
                {
                    return false;
                }
                DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
                Period = period;
                Order = owner._sequence++;
                return true;
            }
        }

        public void Dispose()
        {
            lock (owner._gate)
            {
                _disposed = true;
                owner._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
