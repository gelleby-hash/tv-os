namespace TvLauncher.Tests;

/// <summary>
/// A monotonic clock the test moves by hand. Wall-clock time still comes from the system.
/// Timers (Task.Delay, WaitAsync) fire on the thread pool once Advance reaches their due time. A timer due
/// within <see cref="AutoAdvanceUpTo"/> fires at once instead and moves the clock on to its due time, so short
/// waits such as a 25 ms poll take no real time and need no Advance, while longer ones wait for the test.
/// </summary>
internal sealed class ManualTime : TimeProvider
{
    private readonly object _lock = new();
    private readonly List<ManualTimer> _pending = new();
    private long _ticks;

    /// <summary>Timers due within this fire at once (default: none do).</summary>
    public TimeSpan AutoAdvanceUpTo { get; init; } = TimeSpan.MinValue;

    /// <summary>The time that has passed on this clock.</summary>
    public TimeSpan Elapsed => TimeSpan.FromTicks(GetTimestamp());

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        lock (_lock)
            return _ticks;
    }

    public void Advance(TimeSpan by)
    {
        List<ManualTimer> due;
        lock (_lock)
        {
            _ticks += by.Ticks;
            due = TakeDue();
        }
        Fire(due);
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        // One-shot only (the period is ignored), which is all Task.Delay and WaitAsync ask for.
        var timer = new ManualTimer(this, callback, state);
        Schedule(timer, dueTime);
        return timer;
    }

    private void Schedule(ManualTimer timer, TimeSpan dueTime)
    {
        List<ManualTimer> due;
        lock (_lock)
        {
            _pending.Remove(timer);
            if (dueTime == Timeout.InfiniteTimeSpan)
                return;
            timer.DueAt = _ticks + dueTime.Ticks;
            _pending.Add(timer);
            if (dueTime <= AutoAdvanceUpTo)
                _ticks = Math.Max(_ticks, timer.DueAt);
            due = TakeDue();
        }
        Fire(due);
    }

    private void Cancel(ManualTimer timer)
    {
        lock (_lock)
            _pending.Remove(timer);
    }

    private List<ManualTimer> TakeDue()
    {
        var due = _pending.Where(t => t.DueAt <= _ticks).OrderBy(t => t.DueAt).ToList();
        foreach (var t in due)
            _pending.Remove(t);
        return due;
    }

    private static void Fire(List<ManualTimer> due)
    {
        foreach (var t in due)
            ThreadPool.QueueUserWorkItem(_ => t.Run());
    }

    private sealed class ManualTimer(ManualTime owner, TimerCallback callback, object? state) : ITimer
    {
        public long DueAt { get; set; }

        public void Run() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            owner.Schedule(this, dueTime);
            return true;
        }

        public void Dispose() => owner.Cancel(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
