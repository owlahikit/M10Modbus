namespace M10Modbus
{
    /// <param name="DueTicks">Planned TX moment (Stopwatch ticks).</param>
    /// <param name="Immediate">No anchor yet (first read after start/reconnect): send right away.</param>
    /// <param name="Overrun">The planned slot had already passed: the previous exchange took longer than the period.</param>
    /// <param name="PeriodChanged">The period was changed since the anchor was set.</param>
    public readonly record struct PollSlotPlan(long DueTicks, bool Immediate, bool Overrun, bool PeriodChanged, TimeSpan Period);

    /// <summary>Fixed-step schedule of current-weight reads (D1, R7): next TX = previous planned TX + period.
    /// Wake-up lateness does not accumulate; missed slots are never sent as a catch-up burst.</summary>
    public sealed class PollScheduler
    {
        private static readonly TimeSpan SpinThreshold = TimeSpan.FromMilliseconds(2);
        private static readonly TimeSpan WakeEarly = TimeSpan.FromMilliseconds(1.5);
        private readonly Func<long> _now;
        private readonly long _ticksPerSecond;
        private readonly Func<TimeSpan, CancellationToken, Task> _delay;
        private long? _anchor;
        private TimeSpan _anchorPeriod;

        public PollScheduler(Func<long> now, long ticksPerSecond, Func<TimeSpan, CancellationToken, Task> delay)
        {
            if (ticksPerSecond <= 0) throw new ArgumentOutOfRangeException(nameof(ticksPerSecond));
            _now = now ?? throw new ArgumentNullException(nameof(now));
            _ticksPerSecond = ticksPerSecond;
            _delay = delay ?? throw new ArgumentNullException(nameof(delay));
        }

        public TimeSpan Period { get; set; } = TimeSpan.FromMilliseconds(250);

        public PollSlotPlan NextSlot()
        {
            long now = _now();
            if (!_anchor.HasValue) return new PollSlotPlan(now, true, false, false, Period);
            bool periodChanged = Period != _anchorPeriod;
            long due = _anchor.Value + ToTicks(Period);
            if (now > due) return new PollSlotPlan(now, false, !periodChanged, periodChanged, Period);
            return new PollSlotPlan(due, false, false, periodChanged, Period);
        }

        public async Task WaitAsync(PollSlotPlan plan, CancellationToken ct)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                long remaining = plan.DueTicks - _now();
                if (remaining <= 0) return;
                TimeSpan left = ToTimeSpan(remaining);
                if (left > SpinThreshold)
                {
                    await _delay(left - WakeEarly, ct).ConfigureAwait(false);
                }
                else
                {
                    // Last <= 2 ms: the OS timer is too coarse, spin briefly (worker runs on the thread pool).
                    Thread.SpinWait(20);
                    Thread.Yield();
                }
            }
        }

        public void Commit(PollSlotPlan plan, long actualTxTicks, bool reinitialized)
        {
            bool reanchor = plan.Immediate || plan.Overrun || plan.PeriodChanged || reinitialized;
            _anchor = reanchor ? actualTxTicks : plan.DueTicks;
            _anchorPeriod = Period;
        }

        public void Reset() => _anchor = null;

        private long ToTicks(TimeSpan span) => (long)Math.Round(span.TotalSeconds * _ticksPerSecond);

        private TimeSpan ToTimeSpan(long ticks) => TimeSpan.FromSeconds((double)ticks / _ticksPerSecond);
    }
}
