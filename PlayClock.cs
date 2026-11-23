using System.Text.Json.Serialization;

namespace XivPlaytime;

public sealed record PlayInterval(
    [property: JsonPropertyName("started_at")] long StartedAt,
    [property: JsonPropertyName("ended_at")] long EndedAt);

internal sealed class PlayClock
{
    private const long CheckpointMs = 60_000;
    private const long MaxSampleGapMs = 2_500;
    private long? start;
    private long last;
    private long lastTick;

    public PlayInterval? Sample(bool active, long now, long tick)
    {
        long gap = tick - lastTick;
        if (!active || (start.HasValue &&
            (gap <= 0 || gap > MaxSampleGapMs || Math.Abs(now - last - gap) > 500)))
        {
            var previous = Close();
            if (active)
                Begin(now, tick);
            return previous;
        }
        if (!start.HasValue)
        {
            Begin(now, tick);
            return null;
        }
        last = now;
        lastTick = tick;
        if (last - start.Value < CheckpointMs)
            return null;
        var checkpoint = Close();
        Begin(now, tick);
        return checkpoint;
    }

    public PlayInterval? Close()
    {
        var previous = start is long began && last > began ? new PlayInterval(began, last) : null;
        start = null;
        return previous;
    }

    private void Begin(long now, long tick)
    {
        start = last = now;
        lastTick = tick;
    }
}
