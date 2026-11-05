using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using SteamVrTranslator;

public static class DesktopResponsivenessTests
{
    public static async Task RunAsync()
    {
        await OnUiThread(RenderingDoesNotBlockInput);
        await OnUiThread(AccountResponsesAppearIndependently);
        await OnUiThread(RegistrationKeepsEmailBoundToService);
        await OnUiThread(TargetedRefreshesAvoidUnrelatedRequests);
        await OnUiThread(ClosingDoesNotRunSlowCancellationOnUiThread);
        Console.WriteLine("PASS background subtitle rendering, cancelled bitmap ownership, independent account updates, targeted refreshes and responsive account shutdown");
    }

    private static async Task RenderingDoesNotBlockInput()
    {
        using var form = new MainForm(true);
        try
        {
            int uiThread = Environment.CurrentManagedThreadId, renderingThread = 0;
            using var release = new ManualResetEventSlim();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancel = new CancellationTokenSource();
            Bitmap? cancelledImage = null;
            Task render = form.RenderAndPublishPagesAsync(() =>
            {
                renderingThread = Environment.CurrentManagedThreadId;
                cancelledImage = new Bitmap(4, 4);
                entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Synthetic render was not released");
                return [cancelledImage];
            }, 42, null, cancel.Token);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                Check(renderingThread != uiThread && !render.IsCompleted, "Bitmap work runs away from the UI input thread");
                var inputTurn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                SynchronizationContext.Current!.Post(_ => inputTurn.TrySetResult(), null);
                await inputTurn.Task.WaitAsync(TimeSpan.FromSeconds(3));
                Check(!render.IsCompleted, "The UI handles another message while rendering remains blocked");
                await BackgroundCancellation.RequestAsync(cancel);
            }
            finally { release.Set(); }
            await ExpectCancellation(render);
            Check(Get<List<Bitmap>>(form, "pages").Count == 0, "A cancelled render never publishes a stale subtitle");
            Disposed(cancelledImage!);

            var accepted = new Bitmap(4, 4);
            await form.RenderAndPublishPagesAsync(() => [accepted], 43, null, default);
            Check(ReferenceEquals(Get<List<Bitmap>>(form, "pages").Single(), accepted)
                && Get<long>(form, "pagesGestureId") == 43, "A subsequent render retains bitmap and gesture ownership");
            using var alreadyCancelled = new CancellationTokenSource(); alreadyCancelled.Cancel();
            int calls = 0;
            await ExpectCancellation(form.RenderAndPublishPagesAsync(() => { calls++; return []; }, 44, null, alreadyCancelled.Token));
            Check(calls == 0 && ReferenceEquals(Get<List<Bitmap>>(form, "pages").Single(), accepted),
                "Pre-cancelled work neither renders nor replaces the current subtitles");

            var afterClose = new Bitmap(4, 4);
            Set(form, "closing", true);
            await ExpectCancellation(form.RenderAndPublishPagesAsync(() => [afterClose], 45, null, default));
            Disposed(afterClose);
            Check(ReferenceEquals(Get<List<Bitmap>>(form, "pages").Single(), accepted), "Closing cannot publish a late bitmap");
        }
        finally { Invoke(form, "DisposeServices"); }
    }

    private static async Task AccountResponsesAppearIndependently()
    {
        using var fixture = new AccountFixture();
        var planGate = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        int accountCalls = 0;
        using var form = fixture.Form(async (request, token) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/account")) { accountCalls++; return Reply(new { username = "synthetic", balance = 23, reserved = 2 }); }
            if (path.EndsWith("/ledger")) return Reply(Array.Empty<object>());
            if (path.EndsWith("/orders")) return Reply(Array.Empty<object>());
            if (path.EndsWith("/plans")) return await planGate.Task.WaitAsync(token);
            throw new Exception("Unexpected synthetic account route");
        });
        Task refresh = InvokeTask(form, "RefreshAll");
        try
        {
            await Until(() => Get<Label>(form, "balance").Text.Contains("23", StringComparison.Ordinal));
            Check(accountCalls == 1 && !refresh.IsCompleted, "Available balance appears while an unrelated plans request is pending");
            planGate.SetResult(Reply(new { error = "synthetic plans outage" }, HttpStatusCode.ServiceUnavailable));
            await ExpectCreditFailure(refresh);
            Check(Get<Label>(form, "balance").Text.Contains("23", StringComparison.Ordinal),
                "An unrelated plans failure does not discard the successfully refreshed balance");
        }
        finally
        {
            planGate.TrySetResult(Reply(Array.Empty<object>()));
            try { await refresh; } catch (CreditServiceException) { }
        }
    }

    private static async Task RegistrationKeepsEmailBoundToService()
    {
        using var fixture = new AccountFixture();
        int sends = 0;
        using var form = fixture.Form((request, _) =>
        {
            Check(request.RequestUri!.AbsolutePath == "/v1/auth/register/email-code", "Only Send contacts the service");
            sends++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        });
        form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-20000, -20000); form.ShowInTaskbar = false;
        form.Show();
        var email = Get<TextBox>(form, "email");
        var code = Get<TextBox>(form, "verificationCode");
        var send = Get<Button>(form, "sendCode");
        var submit = Get<Button>(form, "submitAuth");
        Check(!email.Visible && !code.Visible && submit.Enabled, "Password login stays available without email");
        Get<Button>(form, "switchAuth").PerformClick();
        email.Text = "synthetic@example.test"; code.Text = "012345";
        Check(email.Visible && code.Visible && !submit.Enabled && send.Enabled && sends == 0,
            "Unverified inputs do not submit registration or contact a server");
        send.PerformClick();
        await Until(() => !Get<bool>(form, "working"));
        Check(!send.Enabled && send.Text.Contains("秒") && code.Text.Length == 0 && sends == 1,
            "Successful code request starts cooldown and clears any stale code");
        code.Text = "012345";
        Check(submit.Enabled && code.UseSystemPasswordChar, "Requested mailbox can submit a pasted six-digit code");
        email.Text = "different@example.test";
        Check(!submit.Enabled && code.Text.Length == 0 && send.Enabled, "Changing mailbox clears the code and previous send confirmation");
        email.Text = "synthetic@example.test"; code.Text = "012345";
        Check(!send.Enabled && submit.Enabled && sends == 1, "Editing and restoring mailbox does not reset its resend cooldown");
        Get<TextBox>(form, "endpoint").Text = "https://different-service.invalid/";
        Invoke(form, "ApplyEndpoint"); Get<Button>(form, "switchAuth").PerformClick();
        email.Text = "synthetic@example.test"; code.Text = "012345";
        Check(send.Enabled && !submit.Enabled && sends == 1, "Service change clears previous endpoint's code and confirmation");
    }

    private static async Task TargetedRefreshesAvoidUnrelatedRequests()
    {
        using var fixture = new AccountFixture();
        var requests = new List<string>();
        using var form = fixture.Form((request, _) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            requests.Add(path + request.RequestUri.Query);
            object response = path.EndsWith("/redemptions")
                ? new { credits = 5, balance = 25, reserved = 0, alreadyRedeemed = false, reference = "redemption:synthetic" }
                : path.EndsWith("/account") ? new { username = "synthetic", balance = 25, reserved = 0 }
                : path.EndsWith("/orders/fixture-order") ? Order()
                : Array.Empty<object>();
            return Task.FromResult(Reply(response));
        });
        Get<TextBox>(form, "redemptionCode").Text = "MU-0123-4567-89AB-CDEF-0123-4567-89AB-CDEF";
        await InvokeTask(form, "Redeem");
        Check(requests.Order().SequenceEqual(new[] { "/v1/account", "/v1/ledger", "/v1/redemptions" }),
            "Redemption refreshes balance and ledger without fetching unrelated plans or orders");
        Check(Get<TextBox>(form, "redemptionCode").Text.Length == 0, "Completed redemption leaves no code in the control");

        requests.Clear();
        await InvokeTask(form, "RefreshOrders");
        Check(requests.SequenceEqual(["/v1/orders"]), "The order-list button performs only its requested query");

        var orders = Get<ListView>(form, "orders");
        _ = orders.Handle;
        var record = new CreditOrder { Id = "fixture-order", Status = "pending", PayUrl = "https://afdian.com/order/create",
            Credits = 5, AmountFen = 100, CreatedAt = DateTimeOffset.UtcNow };
        var item = new ListViewItem("fixture") { Tag = record };
        orders.Items.Add(item); item.Selected = true;
        requests.Clear();
        await InvokeTask(form, "RefreshSelectedOrder");
        Check(requests.Count == 3 && requests[0] == "/v1/orders/fixture-order?refresh=true"
            && requests.Skip(1).Order().SequenceEqual(new[] { "/v1/account", "/v1/ledger" }),
            "Order verification reuses its returned order and refreshes only the balance and ledger");
    }

    private static async Task ClosingDoesNotRunSlowCancellationOnUiThread()
    {
        using var fixture = new AccountFixture();
        bool clientDisposed = false;
        var form = fixture.Form((_, _) => throw new Exception("Shutdown must not start a request"), () => clientDisposed = true);
        Invoke(form, "EnsureClient");
        var lifetime = Get<CancellationTokenSource>(form, "lifetime");
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int callbackThread = 0, uiThread = Environment.CurrentManagedThreadId;
        using var registration = lifetime.Token.Register(() =>
        {
            callbackThread = Environment.CurrentManagedThreadId;
            entered.TrySetResult();
            try { if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Synthetic shutdown callback not released"); }
            finally { finished.TrySetResult(); }
        });
        try
        {
            form.Dispose();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Check(lifetime.IsCancellationRequested && callbackThread != uiThread && !finished.Task.IsCompleted,
                "Closing returns with cancellation marked while slow callbacks continue off the UI thread");
            Check(!clientDisposed, "Client disposal waits until cancellation callbacks finish using it");
        }
        finally
        {
            release.Set();
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await Until(() => clientDisposed);
            form.Dispose();
        }
    }

    private static object Order() => new { id = "fixture-order", status = "paid", payUrl = (string?)null, credits = 5, amountFen = 100, createdAt = DateTimeOffset.UtcNow };
    private static HttpResponseMessage Reply(object value, HttpStatusCode status = HttpStatusCode.OK) => new(status)
        { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private static async Task Until(Func<bool> ready)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!ready()) await Task.Delay(10, deadline.Token);
    }
    private static async Task ExpectCancellation(Task action)
    { try { await action; } catch (OperationCanceledException) { return; } throw new Exception("Expected cancelled UI work"); }
    private static async Task ExpectCreditFailure(Task action)
    { try { await action; } catch (CreditServiceException) { return; } throw new Exception("Expected synthetic transport failure"); }
    private static void Disposed(Bitmap bitmap)
    {
        try { _ = bitmap.Width; }
        catch (Exception error) when (error is ArgumentException or ObjectDisposedException) { return; }
        throw new Exception("Cancelled rendering retained an unpublished bitmap");
    }
    private static object? Invoke(object target, string name) => target.GetType()
        .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null)!.Invoke(target, null);
    private static Task InvokeTask(object target, string name) => (Task)Invoke(target, name)!;
    private static T Get<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }

    private static Task OnUiThread(Func<Task> test)
    {
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            using var context = new ApplicationContext();
            using var dispatcher = new Control();
            _ = dispatcher.Handle;
            dispatcher.BeginInvoke(async () =>
            {
                try { await test(); complete.TrySetResult(); }
                catch (Exception error) { complete.TrySetException(error); }
                finally { context.ExitThread(); }
            });
            Application.Run(context);
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return complete.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private sealed class AccountFixture : IDisposable
    {
        private const string Address = "https://credit-ui-fixture.invalid/";
        private readonly string directory = Path.Combine(Path.GetTempPath(), "MugonzeiCreditUi-" + Guid.NewGuid().ToString("N"));
        private readonly CreditSessionStore sessions;
        public AccountFixture()
        {
            sessions = new CreditSessionStore(directory);
            sessions.Save(Address, "synthetic-session-only", "synthetic", DateTimeOffset.UtcNow.AddHours(1));
        }
        public CreditAccountForm Form(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send, Action? onDispose = null) =>
            new(Address, url => new CreditServiceClient(url, sessions, new Handler(send, onDispose)));
        public void Dispose()
        {
            foreach (string file in Directory.GetFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
        }
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send, Action? onDispose) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
        protected override void Dispose(bool disposing) { if (disposing) onDispose?.Invoke(); base.Dispose(disposing); }
    }
}
