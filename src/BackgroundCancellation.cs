namespace SteamVrTranslator;

/// <summary>Marks a captured source as cancelled immediately without running its callbacks on the input thread.</summary>
internal static class BackgroundCancellation
{
    public static Task RequestAsync(CancellationTokenSource? source, Action<Exception>? onFailure = null)
    {
        if (source is null) return Task.CompletedTask;
        try
        {
            // CancelAsync changes IsCancellationRequested before returning, then schedules callbacks.
            // Do not put this call inside Task.Run: a queued cancellation could publish an old result.
            return ObserveAsync(source.CancelAsync(), onFailure);
        }
        catch (ObjectDisposedException)
        {
            // The operation already completed and released its cancellation source.
            return Task.CompletedTask;
        }
        catch (Exception error)
        {
            Report(error, onFailure);
            return Task.CompletedTask;
        }
    }

    private static async Task ObserveAsync(Task cancellation, Action<Exception>? onFailure)
    {
        try { await cancellation.ConfigureAwait(false); }
        catch (ObjectDisposedException) { }
        catch (Exception error) { Report(error, onFailure); }
    }

    private static void Report(Exception error, Action<Exception>? onFailure)
    {
        if (onFailure is null) return;
        // Cancellation callbacks are aggregated by the runtime. Keep every failure observable,
        // even when the optional diagnostic sink itself throws while reporting an earlier one.
        if (error is AggregateException aggregate)
        {
            foreach (Exception inner in aggregate.Flatten().InnerExceptions) Notify(inner, onFailure);
        }
        else Notify(error, onFailure);
    }

    private static void Notify(Exception error, Action<Exception> onFailure)
    {
        try { onFailure(error); }
        catch (Exception) { /* Diagnostics must never break controller input or fault the observer task. */ }
    }
}
