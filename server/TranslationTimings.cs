namespace Mugonzei.Server;

internal sealed record PublicTimings(int Samples, double? QueueMs, double? ModelMs, double? SettlementMs);

internal sealed class TranslationTimings
{
    private const int Capacity = 32;
    private readonly Queue<(double Queue, double Model, double Settlement)> samples = new(Capacity);

    public void Add(TimeSpan queue, TimeSpan model, TimeSpan settlement)
    {
        lock (samples)
        {
            if (samples.Count == Capacity) samples.Dequeue();
            samples.Enqueue((queue.TotalMilliseconds, model.TotalMilliseconds, settlement.TotalMilliseconds));
        }
    }

    public PublicTimings Snapshot()
    {
        lock (samples)
        {
            if (samples.Count == 0) return new(0, null, null, null);
            return new(samples.Count, Math.Round(samples.Average(s => s.Queue), 1),
                Math.Round(samples.Average(s => s.Model), 1), Math.Round(samples.Average(s => s.Settlement), 1));
        }
    }
}
