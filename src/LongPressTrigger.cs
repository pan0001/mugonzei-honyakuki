namespace SteamVrTranslator;

/// <summary>
/// A two-stage hold: first translate, then cancel that gesture if the button remains held.
/// A valid release is required after construction, inactivity, reset or clock regression.
/// </summary>
internal sealed class LongPressTrigger
{
    private enum State { Disarmed, Ready, Holding, Translated, Finished }

    private readonly long holdMilliseconds;
    private State state;
    private long pressedAt;
    private long? lastSampleAt;

    public bool CancelRequested { get; private set; }
    public bool IsHolding => state is State.Holding or State.Translated;
    public float Progress { get; private set; }
    public bool CancelStage { get; private set; }
    public long GestureId { get; private set; }

    public LongPressTrigger(long holdMilliseconds = 1500)
    {
        if (holdMilliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(holdMilliseconds));
        this.holdMilliseconds = holdMilliseconds;
    }

    public bool Update(bool active, bool pressed, long nowMilliseconds)
    {
        CancelRequested = false;
        if (lastSampleAt.HasValue && nowMilliseconds < lastSampleAt.Value)
        {
            Reset();
            lastSampleAt = nowMilliseconds;
            return false;
        }
        lastSampleAt = nowMilliseconds;

        if (!active)
        {
            state = State.Disarmed;
            ClearVisualState();
            return false;
        }
        if (!pressed)
        {
            state = State.Ready;
            ClearVisualState();
            return false;
        }
        if (state == State.Ready)
        {
            // Exhaustion is practically unreachable, but never reuse a gesture ID.
            if (GestureId == long.MaxValue)
            {
                state = State.Disarmed;
                ClearVisualState();
                return false;
            }
            GestureId++;
            pressedAt = nowMilliseconds;
            state = State.Holding;
        }
        if (!IsHolding) return false;

        // Samples are monotonic here. Unsigned subtraction also handles a duration
        // spanning more than long.MaxValue. Division avoids overflowing 2 * threshold.
        ulong elapsed = unchecked((ulong)(nowMilliseconds - pressedAt));
        ulong threshold = (ulong)holdMilliseconds;
        if (elapsed / threshold >= 2)
        {
            // A stalled poll past both thresholds must not start obsolete work first.
            state = State.Finished;
            CancelRequested = true;
            CancelStage = true;
            Progress = 1;
            return false;
        }
        if (elapsed >= threshold)
        {
            bool translate = state == State.Holding;
            state = State.Translated;
            CancelStage = true;
            Progress = (float)((double)(elapsed - threshold) / threshold);
            return translate;
        }
        CancelStage = false;
        Progress = (float)((double)elapsed / threshold);
        return false;
    }

    public void Reset()
    {
        state = State.Disarmed;
        pressedAt = 0;
        lastSampleAt = null;
        ClearVisualState();
    }

    private void ClearVisualState()
    {
        CancelRequested = false;
        Progress = 0;
        CancelStage = false;
    }
}
