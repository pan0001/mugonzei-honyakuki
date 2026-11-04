using System.Collections.Concurrent;

namespace SteamVrTranslator;

public static class BackgroundCancellationTests
{
    public static async Task RunAsync()
    {
        await SlowCallbackDoesNotBlockCallerAsync();
        await EveryCallbackFailureIsObservedAsync();
        await NewRequestStaysIndependentAsync();
        await CompletedSourcesAreHarmlessAsync();
        Console.WriteLine("PASS immediate cancellation state, background callbacks, observed failures and independent replacement requests");
    }

    private static async Task SlowCallbackDoesNotBlockCallerAsync()
    {
        using var source = new CancellationTokenSource();
        using var releaseCallback = new ManualResetEventSlim(false);
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? completion = null;
        Exception? callFailure = null;
        bool cancelledWhenReturned = false;
        int callerId = 0, callbackId = 0;
        using var registration = source.Token.Register(() =>
        {
            callbackId = Environment.CurrentManagedThreadId;
            callbackEntered.TrySetResult();
            if (!releaseCallback.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Test did not release the deliberately blocked cancellation callback.");
        });
        var inputThread = new Thread(() =>
        {
            callerId = Environment.CurrentManagedThreadId;
            try
            {
                completion = BackgroundCancellation.RequestAsync(source);
                cancelledWhenReturned = source.IsCancellationRequested;
            }
            catch (Exception error) { callFailure = error; }
            finally { callReturned.TrySetResult(); }
        }) { IsBackground = true };
        inputThread.Start();
        try
        {
            // A synchronous Cancel() implementation cannot return until the still-closed gate opens.
            await callReturned.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Check(callFailure is null, "RequestAsync itself cannot throw back into the input callback");
            Check(cancelledWhenReturned, "The cancellation flag is set before RequestAsync returns");
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Check(callbackId != callerId, "Slow cleanup runs on a different thread from controller input");
            Check(completion is not null && !completion.IsCompleted,
                "The caller has returned while the callback is still blocked and its completion remains observable");
        }
        finally
        {
            releaseCallback.Set();
            await callReturned.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (completion is not null) await completion.WaitAsync(TimeSpan.FromSeconds(5));
            Check(inputThread.Join(1000), "The input thread exits without being retained by callback work");
        }
    }

    private static async Task EveryCallbackFailureIsObservedAsync()
    {
        using var source = new CancellationTokenSource();
        var first = new InvalidOperationException("first callback failure");
        var second = new ArithmeticException("second callback failure");
        var observed = new ConcurrentQueue<Exception>();
        int completedCallbacks = 0;
        using var good = source.Token.Register(() => Interlocked.Increment(ref completedCallbacks));
        using var badOne = source.Token.Register(() => throw first);
        using var badTwo = source.Token.Register(() => throw new AggregateException(second));
        Task completion = BackgroundCancellation.RequestAsync(source, error =>
        {
            observed.Enqueue(error);
            throw new InvalidOperationException("The diagnostic sink also fails.");
        });
        await completion.WaitAsync(TimeSpan.FromSeconds(5));
        Check(completion.IsCompletedSuccessfully, "Callback and logging failures cannot fault the returned observer task");
        Check(observed.Count == 2 && observed.Contains(first) && observed.Contains(second),
            "Every individual cancellation callback failure reaches the diagnostic sink despite sink exceptions");
        Check(completedCallbacks == 1, "Throwing cleanup does not skip other registered cancellation callbacks");

        using var silent = new CancellationTokenSource();
        using var throwing = silent.Token.Register(() => throw new InvalidOperationException("No logger configured."));
        Task silentCompletion = BackgroundCancellation.RequestAsync(silent);
        await silentCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        Check(silentCompletion.IsCompletedSuccessfully, "Failures are observed even when the caller does not supply a logger");
    }

    private static async Task NewRequestStaysIndependentAsync()
    {
        using var previous = new CancellationTokenSource();
        Task oldCompletion = BackgroundCancellation.RequestAsync(previous);
        Check(previous.IsCancellationRequested, "Old request is cancelled immediately");
        using var replacement = new CancellationTokenSource();
        int replacementCallbacks = 0;
        using var registration = replacement.Token.Register(() => Interlocked.Increment(ref replacementCallbacks));
        await oldCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        await BackgroundCancellation.RequestAsync(previous);
        Check(!replacement.IsCancellationRequested && replacementCallbacks == 0,
            "Completing or repeating an old cancellation cannot cancel a newer independent request");
        await BackgroundCancellation.RequestAsync(replacement).WaitAsync(TimeSpan.FromSeconds(5));
        Check(replacement.IsCancellationRequested && replacementCallbacks == 1,
            "The next request still has a working, independently scoped cancellation source");
    }

    private static async Task CompletedSourcesAreHarmlessAsync()
    {
        int reports = 0;
        await BackgroundCancellation.RequestAsync(null, _ => reports++);
        var disposed = new CancellationTokenSource();
        disposed.Dispose();
        await BackgroundCancellation.RequestAsync(disposed, _ => reports++);
        Check(reports == 0, "A missing or already disposed operation is harmless, not a callback failure");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("BackgroundCancellationTests: " + message);
    }
}
