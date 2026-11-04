using System.Reflection;
using SteamVrTranslator;

public static class GestureCancelTests
{
    public static void Run()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            MainForm? form = null;
            try
            {
                form = new MainForm(true);
                using var active = new CancellationTokenSource();
                Set(form, "currentRequest", active);
                Set(form, "activeGestureId", 12L);
                var pages = Get<List<Bitmap>>(form, "pages");
                pages.Add(new Bitmap(4, 4));
                Set(form, "pagesGestureId", 12L);
                Set(form, "overlayGestureId", 12L);
                form.CancelGesture(11);
                Check(!active.IsCancellationRequested && pages.Count == 1, "An older hold must preserve the newer active translation and subtitles");
                form.CancelGesture(12);
                Check(active.IsCancellationRequested && pages.Count == 0, "The owning second circle cancels active inference and removes its subtitles");
                Check(Get<long>(form, "overlayGestureId") == 0, "Cancelled subtitles cannot remain marked visible");
                form.ReportCaptureFailure(new InvalidOperationException("late provider failure"), active.Token);
                Check(Get<Label>(form, "status").Text == "已取消。", "A provider error racing with cancellation must not publish a new error subtitle");

                using var keyboard = new CancellationTokenSource();
                Set(form, "currentRequest", keyboard);
                Set(form, "activeGestureId", 0L);
                pages.Add(new Bitmap(4, 4));
                Set(form, "pagesGestureId", 0L);
                form.CancelGesture(12);
                Check(!keyboard.IsCancellationRequested && pages.Count == 1, "A completed hold must not cancel a later keyboard request");

                Set(form, "currentRequest", null);
                Set(form, "pagesGestureId", 13L);
                Set(form, "overlayGestureId", 13L);
                form.CancelGesture(13);
                Check(pages.Count == 0 && Get<long>(form, "overlayGestureId") == 0,
                    "Second circle also removes a translation that already completed before three seconds");

                using var callbackEntered = new ManualResetEventSlim();
                using var finishCallback = new ManualResetEventSlim();
                using var callbackFinished = new ManualResetEventSlim();
                using var slow = new CancellationTokenSource();
                using var registration = slow.Token.Register(() =>
                {
                    callbackEntered.Set();
                    try { finishCallback.Wait(TimeSpan.FromSeconds(5)); throw new AggregateException(new IOException("simulated process-tree failure")); }
                    finally { callbackFinished.Set(); }
                });
                Set(form, "currentRequest", slow); Set(form, "activeGestureId", 14L);
                try
                {
                    form.CancelGesture(14);
                    Check(slow.IsCancellationRequested && !callbackFinished.IsSet,
                        "Cancellation marks old work immediately without waiting on process termination on the input thread");
                    Check(callbackEntered.Wait(TimeSpan.FromSeconds(2)), "Background termination callback begins");
                    using var next = new CancellationTokenSource();
                    Set(form, "currentRequest", next); Set(form, "activeGestureId", 15L);
                    form.CancelGesture(14);
                    Check(!next.IsCancellationRequested, "Old slow cleanup cannot cancel the next controller request");
                    form.CancelGesture(15);
                    Check(next.IsCancellationRequested, "The next gesture remains independently cancellable");
                }
                finally { finishCallback.Set(); Check(callbackFinished.Wait(TimeSpan.FromSeconds(2)), "Callback cleanup finishes"); Set(form, "currentRequest", null); }
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                if (form is not null)
                {
                    typeof(MainForm).GetMethod("DisposeServices", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, null);
                    form.Dispose();
                }
            }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) throw new Exception("Gesture cancellation integration failed", failure);
        Console.WriteLine("PASS gesture cancellation targets current/pending/completed work without cancelling newer keyboard requests");
    }

    private static void Set(MainForm form, string name, object? value) =>
        typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(form, value);
    private static T Get<T>(MainForm form, string name) =>
        (T)typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
    private static void Check(bool success, string message)
    {
        if (!success) throw new Exception(message);
    }
}
