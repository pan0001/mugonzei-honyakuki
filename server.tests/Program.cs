using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Mugonzei.Server;

internal static partial class Program
{
    private const string Pixel = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aN1kAAAAASUVORK5CYII=";
    private static int checks;
    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.FirstOrDefault() == "--serve-e2e") return await ServeEmailFixture(args);
            await PaymentAccounting();
            await CouponTests();
            await CouponCheckoutHttpTests();
            await PaymentVerification();
            RefundAccounting();
            await AuthenticationPersistence();
            await RegistrationEmailTests();
            await ReservationsAndRecovery();
            await WorkerLifecycle();
            await QueuedCancellationReleasesCapacity();
            await QueueWorkersAndShutdown();
            await QueueSubmissionCancellationRaces();
            await CancellationBeforeSubmission();
            await CancellationRaces();
            await ProcessorTransport();
            await HttpContract();
            await RedemptionTests();
            Console.WriteLine($"PASS official server: {checks} assertions; synthetic data and local/fake transports only.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static async Task PaymentAccounting()
    {
        using var fixture = new Fixture();
        string user = fixture.User(), other = fixture.User();
        var plan = new Plan("fixture", "Synthetic credits", 10, 100, "fixture-plan");
        string requestId = Guid.NewGuid().ToString();
        var order = fixture.Store.CreateOrder(user, requestId, plan);
        Equal(order.Id, fixture.Store.CreateOrder(user, requestId, plan).Id, "Order creation retries are idempotent");
        Throws(409, () => fixture.Store.CreateOrder(user, requestId, plan with { Id = "changed" }));
        Throws(404, () => fixture.Store.GetOrder(other, order.Id));
        Throws(409, () => fixture.Store.CreditOrder(order.Id, "fixture-trade", 99, order.AfdianPlanId, order.CustomId));
        Throws(409, () => fixture.Store.CreditOrder(order.Id, "fixture-trade", 100, "other-plan", order.CustomId));
        Throws(409, () => fixture.Store.CreditOrder(order.Id, "fixture-trade", 100, order.AfdianPlanId, "other-binding"));
        Equal(0L, fixture.Store.GetAccount(user).Balance, "Mismatched payment claims grant no credits");
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            fixture.Store.CreditOrder(order.Id, "fixture-trade", 100, order.AfdianPlanId, order.CustomId))));
        Equal(10L, fixture.Store.GetAccount(user).Balance, "Concurrent duplicate notifications credit once");
        Equal(1, fixture.Store.GetLedger(user).Count, "Payment has exactly one ledger record");
        var second = fixture.Store.CreateOrder(other, Guid.NewGuid().ToString(), plan);
        Throws(409, () => fixture.Store.CreditOrder(second.Id, "fixture-trade", 100, second.AfdianPlanId, second.CustomId));
        Equal(0L, fixture.Store.GetAccount(other).Balance, "A provider trade cannot fund another user");
    }

    private static async Task AuthenticationPersistence()
    {
        using var fixture = new Fixture();
        var auth = new AuthService(fixture.Store);
        var credentials = new Credentials("fixture_user", "synthetic-password-012345");
        var inbox = new TestMailbox();
        var clock = new RegistrationClock();
        var settings = TestEmailOptions();
        var email = new RegistrationEmailService(fixture.Store, settings, inbox, clock);
        const string address = "owner@example.test";
        await email.RequestAsync(address, IPAddress.Loopback, default);
        var request = new RegistrationRequest(credentials.Username, credentials.Password, address, inbox.LastCode);
        AuthReply registered = auth.Register(request, email);
        var persisted = new Store(fixture.Path);
        var afterRestart = new AuthService(persisted);
        var context = Context(registered.Token);
        afterRestart.Authenticate(context);
        string user = AuthService.UserId(context);
        Equal("fixture_user", persisted.GetAccount(user).Username, "Saved session survives a new service instance");
        Check(!persisted.FindUser("fixture_user")!.PasswordHash.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(credentials.Password)), "Password is not persisted as plaintext");
        Throws(401, () => afterRestart.Login(credentials with { Password = "wrong-password-012345" }));
        Throws(400, () => afterRestart.Register(request, new RegistrationEmailService(persisted, settings, inbox, clock)));
        AuthReply loggedIn = afterRestart.Login(credentials);
        Check(registered.Token != loggedIn.Token, "Login issues an independent random session");
        afterRestart.Logout(context);
        var revoked = Context(registered.Token); afterRestart.Authenticate(revoked);
        Throws(401, () => AuthService.UserId(revoked));
        var live = Context(loggedIn.Token); afterRestart.Authenticate(live);
        Equal(user, AuthService.UserId(live), "Logging out one session leaves another session intact");
        var forged = Context(new string('A', 43)); afterRestart.Authenticate(forged);
        Throws(401, () => AuthService.UserId(forged));
    }

    private static async Task PaymentVerification()
    {
        using var fixture = new Fixture(); string user = fixture.User();
        var plan = new Plan("live-fixture", "Synthetic", 3, 125, "fixture-plan");
        var provider = new FakePaymentProvider();
        var payments = new PaymentService(fixture.Store, new ServerOptions { LivePayments = true, Plans = [plan] }, provider);
        var order = payments.CreateOrder(user, Guid.NewGuid().ToString(), plan.Id);
        await payments.NotifyAsync("forged-trade", default);
        Equal(0L, fixture.Store.GetAccount(user).Balance, "An unverified webhook trade does not credit the claimed purchase");
        Equal("forged-trade", provider.Requested.Last(), "Webhook triggers an independent provider query");
        provider.Orders = [new("different-trade", order.CustomId, order.AfdianPlanId, order.AmountFen, 2)];
        await payments.NotifyAsync("forged-trade", default);
        Equal(0L, fixture.Store.GetAccount(user).Balance, "A queried order with another trade number cannot validate the callback");
        provider.Orders = [new("verified-trade", order.CustomId, order.AfdianPlanId, order.AmountFen, 1)];
        await payments.NotifyAsync("verified-trade", default);
        Equal(0L, fixture.Store.GetAccount(user).Balance, "Pending provider orders do not grant credits");
        provider.Orders = [new("verified-trade", order.CustomId, order.AfdianPlanId, order.AmountFen - 1, 2)];
        await ThrowsApiAsync(409, () => payments.NotifyAsync("verified-trade", default));
        provider.Orders = [new("verified-trade", order.CustomId, order.AfdianPlanId, order.AmountFen, 2)];
        await payments.NotifyAsync("verified-trade", default); await payments.NotifyAsync("verified-trade", default);
        Equal(3L, fixture.Store.GetAccount(user).Balance, "Verified callback retries credit once");
        Throws(404, () => payments.ConfirmMock(order.Id));
        var other = fixture.User();
        await ThrowsApiAsync(404, () => payments.RefreshAsync(other, order.Id, default));
        Equal(0L, fixture.Store.GetAccount(other).Balance, "Cross-account order lookup never credits another user");

        var pagedProvider = new FakePaymentProvider { PageOrders = new() };
        var pagedPayments = new PaymentService(fixture.Store, new ServerOptions { LivePayments = true, Plans = [plan] }, pagedProvider);
        var delayedOrder = pagedPayments.CreateOrder(user, Guid.NewGuid().ToString(), plan.Id);
        pagedProvider.PageOrders[2] = [new("second-page-trade", delayedOrder.CustomId, delayedOrder.AfdianPlanId, delayedOrder.AmountFen, 2)];
        Equal("paid", (await pagedPayments.RefreshAsync(user, delayedOrder.Id, default)).Status,
            "An empty filtered first page does not hide a paid order on the next page");
        Check(pagedProvider.RequestedPages.SequenceEqual([1, 2]), "Reconciliation scans through the empty first page and stops when paid");
        Equal(6L, fixture.Store.GetAccount(user).Balance, "A payment found on page two credits once");
        await pagedPayments.RefreshAsync(user, delayedOrder.Id, default);
        Equal(6L, fixture.Store.GetAccount(user).Balance, "Refreshing the reconciled order does not credit again");
        Check(pagedProvider.RequestedPages.SequenceEqual([1, 2]), "A paid order does not issue another reconciliation scan");

        var emptyProvider = new FakePaymentProvider { PageOrders = new() };
        var emptyPayments = new PaymentService(fixture.Store, new ServerOptions { LivePayments = true, Plans = [plan] }, emptyProvider);
        var pendingOrder = emptyPayments.CreateOrder(user, Guid.NewGuid().ToString(), plan.Id);
        Equal("pending", (await emptyPayments.RefreshAsync(user, pendingOrder.Id, default)).Status,
            "An absent payment remains pending after the bounded scan");
        Check(emptyProvider.RequestedPages.SequenceEqual([1, 2, 3]), "An all-empty reconciliation still scans all three permitted pages");
        Equal(6L, fixture.Store.GetAccount(user).Balance, "Empty reconciliation pages never grant credits");
    }

    private static void RefundAccounting()
    {
        using var fixture = new Fixture(); string user = fixture.User(5);
        var order = fixture.Store.GetOrders(user).Single(); string released = Guid.NewGuid().ToString();
        fixture.Store.HoldRefund(order.Id, released); fixture.Store.HoldRefund(order.Id, released);
        Equal(5L, fixture.Store.GetAccount(user).Reserved, "Repeated refund holds reserve only once");
        Equal(0, fixture.Store.RecoverTranslations(), "Translation recovery ignores refund reservations");
        Equal(5L, fixture.Store.GetAccount(user).Reserved, "Restart does not release a pending manual refund");
        Throws(402, () => fixture.Store.ReserveTranslation(user, Guid.NewGuid().ToString(), "hash", "{}"));
        Equal("cancelled", fixture.Store.ResolveRefund(released, false).Status, "Abandoned refund releases its hold");
        fixture.Store.ResolveRefund(released, false);
        Equal(0L, fixture.Store.GetAccount(user).Reserved, "Repeated refund release cannot release twice");
        Equal(5L, fixture.Store.GetAccount(user).Balance, "Releasing a refund leaves posted credits intact");
        string confirmed = Guid.NewGuid().ToString(); fixture.Store.HoldRefund(order.Id, confirmed);
        fixture.Store.ResolveRefund(confirmed, true); fixture.Store.ResolveRefund(confirmed, true);
        Equal(0L, fixture.Store.GetAccount(user).Balance, "Confirmed refund removes credits exactly once");
        Equal(0L, fixture.Store.GetAccount(user).Reserved, "Confirmed refund clears its hold");
        fixture.Store.CreditOrder(order.Id, "late-webhook", order.AmountFen, order.AfdianPlanId, order.CustomId);
        Equal(0L, fixture.Store.GetAccount(user).Balance, "Late payment replay does not re-credit a refunded order");
        string consumed = fixture.User(2); var consumedOrder = fixture.Store.GetOrders(consumed).Single(); string task = Guid.NewGuid().ToString();
        fixture.Store.ReserveTranslation(consumed, task, "hash", "{}"); fixture.Store.CompleteTranslation(consumed, task, ResultJson(), true);
        Throws(409, () => fixture.Store.HoldRefund(consumedOrder.Id, Guid.NewGuid().ToString()));
        Equal(1L, fixture.Store.GetAccount(consumed).Balance, "Insufficient refundable balance leaves the account unchanged");
        using var lease = new DatabaseLease(fixture.Path);
        try { using var duplicate = new DatabaseLease(fixture.Path); throw new Exception("A second database process acquired the lease"); }
        catch (InvalidOperationException) { checks++; }
    }

    private static async Task ReservationsAndRecovery()
    {
        using var fixture = new Fixture(); string user = fixture.User(1), other = fixture.User();
        string first = Guid.NewGuid().ToString(), second = Guid.NewGuid().ToString();
        var attempts = await Task.WhenAll(new[] { first, second }.Select(id => Task.Run(() =>
        {
            try { return fixture.Store.ReserveTranslation(user, id, "hash", "{}").Job; }
            catch (ApiException error) when (error.StatusCode == 402) { return null; }
        })));
        Equal(1, attempts.Count(job => job is not null), "One available credit permits one concurrent reservation");
        var accepted = attempts.Single(job => job is not null)!;
        Equal(1L, fixture.Store.GetAccount(user).Reserved, "A reservation does not deduct the posted balance");
        Equal(1L, fixture.Store.GetAccount(user).Balance, "Only successful work debits the account");
        Check(!fixture.Store.ReserveTranslation(user, accepted.Id, "hash", "{}").Created, "Repeated reservation returns existing work");
        Throws(409, () => fixture.Store.ReserveTranslation(user, accepted.Id, "changed", "{}"));
        Throws(404, () => fixture.Store.GetTranslation(other, accepted.Id));
        Throws(404, () => fixture.Store.FailTranslation(other, accepted.Id, "cancelled", "cancelled"));
        fixture.Store.StartTranslation(user, accepted.Id);
        var restarted = new Store(fixture.Path);
        Equal(1, restarted.RecoverTranslations(), "Restart marks interrupted work failed");
        Equal(0, restarted.RecoverTranslations(), "Recovery itself is idempotent");
        Equal("server_restarted", restarted.GetTranslation(user, accepted.Id).ErrorCode, "Recovered failure tells clients what happened");
        Equal(0L, restarted.GetAccount(user).Reserved, "Recovery releases the reservation");
        Equal(1L, restarted.GetAccount(user).Balance, "Recovery does not charge");
        restarted.CompleteTranslation(user, accepted.Id, ResultJson(), true);
        Equal(1L, restarted.GetAccount(user).Balance, "Late completion after recovery cannot charge");
        string success = Guid.NewGuid().ToString();
        restarted.ReserveTranslation(user, success, "success", "{\"text\":\"synthetic source\"}");
        restarted.CompleteTranslation(user, success, ResultJson(), true);
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = fixture.Path }.ToString()))
        {
            db.Open(); using var update = db.CreateCommand();
            update.CommandText = "UPDATE translations SET updated_at=$old WHERE id=$id";
            update.Parameters.AddWithValue("$old", DateTimeOffset.UtcNow.AddDays(-8).ToString("O"));
            update.Parameters.AddWithValue("$id", Guid.Parse(success).ToString("N")); update.ExecuteNonQuery();
        }
        restarted.PruneExpiredData();
        var expired = restarted.GetTranslation(user, success);
        Equal("result_expired", expired.ErrorCode, "Expired results explain their absence");
        Check(expired.ResultJson is null && expired.InputJson == "{}", "Result expiry clears source and translation content");
        Equal(0L, restarted.GetAccount(user).Balance, "Expiring result content does not refund completed work");
    }

    private static async Task WorkerLifecycle()
    {
        using var fixture = new Fixture(); string user = fixture.User(4);
        var fake = new FakeProcessor();
        using var service = Service(fixture.Store, fake);
        await service.StartAsync(default);
        try
        {
            Guid success = Guid.NewGuid(); var input = TextInput("日本語 hello 中文");
            service.Submit(user, success, input); // Deliberately ignore the first response.
            var result = await Terminal(service, user, success);
            Equal("succeeded", result.Status, "Accepted work completes without the caller retaining its response");
            Equal(input.Text, result.SourceText, "The recognized original survives recovery queries");
            Equal(3L, result.Balance, "A successful task costs exactly one credit");
            service.Submit(user, success, input);
            Equal(1, fake.Calls, "Lost response retries do not call the model again");
            Throws(409, () => service.Submit(user, success, TextInput("different")));
            Equal("succeeded", service.Cancel(user, success).Status, "Cancel after completion keeps the completed result");
            Equal(3L, fixture.Store.GetAccount(user).Balance, "Completed work is not refunded by cancellation");
            foreach (string text in new[] { "FAIL", "NO_TEXT", "OVERSIZED" })
            {
                Guid id = Guid.NewGuid(); service.Submit(user, id, TextInput(text));
                var failed = await Terminal(service, user, id);
                Equal(text == "NO_TEXT" ? "no_text" : "failed", failed.Status, "Failed or empty results are explicit terminal states");
                Equal(3L, fixture.Store.GetAccount(user).Balance, "Failed, no-text and oversized outputs cost nothing");
                Equal(0L, fixture.Store.GetAccount(user).Reserved, "Unsuccessful work releases its reservation");
            }
            var image = TranslationInput.Validate(new("image", null, Pixel, "简体中文"));
            Guid imageId = Guid.NewGuid(); service.Submit(user, imageId, image); await Terminal(service, user, imageId);
            string persisted = fixture.Store.GetTranslation(user, imageId.ToString()).InputJson;
            Check(!persisted.Contains(Pixel, StringComparison.Ordinal) && !persisted.Contains("imageBase64", StringComparison.Ordinal)
                && persisted.Contains("imageSha256", StringComparison.Ordinal), "The persisted task never contains image pixels or base64");
        }
        finally { await service.StopAsync(default); }
    }

    private static async Task CancellationRaces()
    {
        using var fixture = new Fixture(); string user = fixture.User(25);
        var blocking = new BlockingProcessor(); using var service = Service(fixture.Store, blocking);
        await service.StartAsync(default);
        try
        {
            Guid id = Guid.NewGuid(); service.Submit(user, id, TextInput("cancel me"));
            await blocking.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Equal("cancelled", service.Cancel(user, id).Status, "Cancel wins while the model is in flight");
            blocking.Finish.TrySetResult(new(false, "cancel me", "取消我"));
            await blocking.Returned.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(30);
            Equal("cancelled", service.Get(user, id).Status, "A late model response cannot overwrite cancellation");
            Equal(25L, fixture.Store.GetAccount(user).Balance, "An effective cancellation costs nothing");
        }
        finally { blocking.Finish.TrySetResult(new(false, "source", "译文")); await service.StopAsync(default); }
        for (int index = 0; index < 20; index++)
        {
            string id = Guid.NewGuid().ToString(); long before = fixture.Store.GetAccount(user).Balance;
            fixture.Store.ReserveTranslation(user, id, "hash", "{}");
            await Task.WhenAll(Task.Run(() => fixture.Store.CompleteTranslation(user, id, ResultJson(), true)),
                Task.Run(() => fixture.Store.FailTranslation(user, id, "cancelled", "cancelled")));
            string status = fixture.Store.GetTranslation(user, id).Status;
            Equal(before - (status == "succeeded" ? 1 : 0), fixture.Store.GetAccount(user).Balance, "Concurrent completion and cancel settle according to one winner");
            Equal(0L, fixture.Store.GetAccount(user).Reserved, "Racing terminal transitions release exactly once");
        }
    }

    private static async Task CancellationBeforeSubmission()
    {
        using var fixture = new Fixture(); string user = fixture.User(25), other = fixture.User(1), outsider = fixture.User();
        var fake = new FakeProcessor(); using var service = Service(fixture.Store, fake);
        await service.StartAsync(default);
        try
        {
            Guid id = Guid.NewGuid(); int ledgerBefore = fixture.Store.GetLedger(user).Count;
            Equal("cancelled", service.Cancel(user, id).Status, "Cancel arriving before upload creates a terminal cancellation");
            Equal("cancelled", service.Submit(user, id, TextInput("late upload")).Status, "A late upload respects a prior cancellation");
            Equal("cancelled", service.Submit(user, id, TextInput("different late upload")).Status,
                "Cancellation does not require the not-yet-arrived request body to match");
            Equal(0, fake.Calls, "A request cancelled before submission never calls the model");
            Equal(25L, fixture.Store.GetAccount(user).Balance, "A cancellation tombstone does not debit credits");
            Equal(0L, fixture.Store.GetAccount(user).Reserved, "A cancellation tombstone does not reserve credits");
            Equal(ledgerBefore, fixture.Store.GetLedger(user).Count, "Cancellation before submission creates no financial ledger entry");
            Throws(404, () => service.Get(outsider, id));
            service.Submit(other, id, TextInput("another account"));
            Equal("succeeded", (await Terminal(service, other, id)).Status, "Another account may independently use the same request identifier");
            Equal(1, fake.Calls, "Only the other account's uncancelled request invokes processing");
            Equal("cancelled", service.Get(user, id).Status, "Another account's completion cannot overwrite the cancellation");
            Equal(0L, fixture.Store.GetAccount(other).Balance, "The independently completed request charges its own account");
        }
        finally { await service.StopAsync(default); }

        for (int index = 0; index < 20; index++)
        {
            string id = Guid.NewGuid().ToString(); long before = fixture.Store.GetAccount(user).Balance;
            await Task.WhenAll(
                Task.Run(() => fixture.Store.CancelTranslation(user, id)),
                Task.Run(() => fixture.Store.ReserveTranslation(user, id, "late-hash", "{}")),
                Task.Run(() =>
                {
                    try { fixture.Store.CompleteTranslation(user, id, ResultJson(), true); }
                    catch (ApiException error) when (error.StatusCode == 404) { /* Completion can arrive before either creator. */ }
                }));
            var terminal = fixture.Store.GetTranslation(user, id);
            Check(terminal.Status is "cancelled" or "succeeded", "Cancel/reserve/complete races always reach one terminal result");
            Equal(0L, fixture.Store.GetAccount(user).Reserved, "Pre-cancel races cannot produce a negative or retained reservation");
            Equal(before - (terminal.Status == "succeeded" ? 1 : 0), fixture.Store.GetAccount(user).Balance,
                "Only the winning completion may debit a raced request");
        }
    }

    private static async Task ProcessorTransport()
    {
        const string source = "あした学校へ\nhello 中文 [无法辨认]";
        var handler = new ReplyHandler([
            Envelope(JsonSerializer.Serialize(new { status = "text", sourceText = source })),
            Envelope(JsonSerializer.Serialize(new { translation = "明天去学校\n你好 中文 [无法辨认]" }))]);
        using var http = new HttpClient(handler);
        using var processor = new TranslationProcessor(Options(), http);
        var input = TranslationInput.Validate(new("handwriting", null, Pixel, "简体中文"));
        var result = await processor.ProcessAsync(input, default);
        Equal(source, result.SourceText, "Mixed-language transcription is retained verbatim");
        Check(result.Translation.Contains("[无法辨认]", StringComparison.Ordinal), "Uncertainty markers survive translation");
        Equal(2, handler.Bodies.Count, "Image reading and text translation use two stages for one task");
        using var first = JsonDocument.Parse(handler.Bodies[0]); using var second = JsonDocument.Parse(handler.Bodies[1]);
        Equal("server-only-model", first.RootElement.GetProperty("model").GetString(), "The model comes from server configuration");
        Equal("Bearer synthetic-server-key", handler.Authorization.Single(), "Only server credentials authenticate the upstream");
        Check(!handler.Bodies[1].Contains(Pixel, StringComparison.Ordinal), "The translation stage does not resend the image");
        Check(handler.Uris.All(uri => uri == "https://upstream.invalid/v1/chat/completions"), "The client cannot choose the upstream destination");
        foreach (string output in new[] { "A person is standing in a room.", "{\"status\":\"scene\",\"sourceText\":\"a room\"}",
            "{\"status\":\"no_text\",\"sourceText\":\"invented\"}", "{\"status\":\"text\",\"sourceText\":\"\"}",
            "{\"status\":\"text\",\"sourceText\":\"x\",\"extra\":true}" })
        {
            using var invalidHttp = new HttpClient(new ReplyHandler([Envelope(output)]));
            using var invalid = new TranslationProcessor(Options(), invalidHttp);
            await ThrowsProcessing(() => invalid.ProcessAsync(input, default));
        }
        var noTextHandler = new ReplyHandler([Envelope("{\"status\":\"no_text\",\"sourceText\":\"\"}")]);
        using var noTextHttp = new HttpClient(noTextHandler); using var noTextProcessor = new TranslationProcessor(Options(), noTextHttp);
        Check((await noTextProcessor.ProcessAsync(input, default)).NoText, "Structured no_text is recognized explicitly");
        Equal(1, noTextHandler.Bodies.Count, "A no-text image does not enter translation");
        using var truncatedHttp = new HttpClient(new ReplyHandler([Envelope("{\"translation\":\"partial\"}", "length")]));
        using var truncated = new TranslationProcessor(Options(), truncatedHttp);
        await ThrowsProcessing(() => truncated.ProcessAsync(TextInput("source"), default));
        using var hugeHttp = new HttpClient(new ReplyHandler([new string('x', 1_048_577)]));
        using var huge = new TranslationProcessor(Options(), hugeHttp);
        await ThrowsProcessing(() => huge.ProcessAsync(input, default));
        Throws(400, () => TranslationInput.Validate(new("image", null, "not-base64", "zh")));
        Throws(400, () => TranslationInput.Validate(new("text", "source", Pixel, "zh")));
        try { JsonSerializer.Deserialize<TranslationRequest>("{\"mode\":\"text\",\"text\":\"x\",\"targetLanguage\":\"zh\",\"model\":\"attacker\"}", TranslationInput.Json); throw new Exception("Unexpected client model accepted"); }
        catch (JsonException) { checks++; }
    }

    private static async Task HttpContract()
    {
        using var fixture = new Fixture();
        var auth = new AuthService(fixture.Store);
        var credentials = new Credentials("http_fixture", "synthetic-password-12345");
        fixture.LegacyUser(credentials);
        var login = auth.Login(credentials);
        var context = Context(login.Token); auth.Authenticate(context); string user = AuthService.UserId(context); fixture.Fund(user, 2);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddSingleton(fixture.Store); builder.Services.AddSingleton<ITranslationProcessor>(new FakeProcessor());
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = 429;
            options.AddFixedWindowLimiter("translation-submit", limit => { limit.PermitLimit = 20; limit.Window = TimeSpan.FromMinutes(1); });
            options.AddFixedWindowLimiter("translation-cancel", limit => { limit.PermitLimit = 60; limit.Window = TimeSpan.FromMinutes(1); });
        });
        TranslationEndpoints.AddTranslationServices(builder.Services, builder.Configuration, builder.Environment);
        await using var app = builder.Build();
        app.Use(async (http, next) =>
        {
            try { auth.Authenticate(http); await next(http); }
            catch (ApiException error) { http.Response.StatusCode = error.StatusCode; await http.Response.WriteAsJsonAsync(new { error = error.Code }); }
        });
        app.UseRateLimiter();
        TranslationEndpoints.MapTranslationEndpoints(app);
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            string path = "/v1/translations/" + Guid.NewGuid();
            using var denied = await client.GetAsync(path); Equal(HttpStatusCode.Unauthorized, denied.StatusCode, "HTTP task queries require a session");
            client.DefaultRequestHeaders.Authorization = new("Bearer", login.Token);
            using var accepted = await client.PutAsJsonAsync(path, new { mode = "text", text = "hello", targetLanguage = "zh" });
            Check(accepted.StatusCode is HttpStatusCode.Accepted or HttpStatusCode.OK, "PUT returns an asynchronous task receipt");
            TranslationTaskDto? completed = null;
            for (int attempt = 0; attempt < 100; attempt++)
            {
                completed = await client.GetFromJsonAsync<TranslationTaskDto>(path);
                if (completed!.Status == "succeeded") break;
                await Task.Delay(10);
            }
            Equal("succeeded", completed!.Status, "GET retrieves the completed result with the same identifier");
            string wireJson = await client.GetStringAsync(path);
            var guidDto = JsonSerializer.Deserialize<ClientTaskContract>(wireJson, TranslationInput.Json)!;
            Equal(Guid.Parse(path.Split('/').Last()), guidDto.Id, "Wire identifiers deserialize into the desktop client's Guid field");
            Equal(1L, completed.Balance, "HTTP success reports the settled balance");
            using var conflict = await client.PutAsJsonAsync(path, new { mode = "text", text = "changed", targetLanguage = "zh" });
            Equal(HttpStatusCode.Conflict, conflict.StatusCode, "HTTP idempotency rejects changed request bodies");
            using var cancelled = await client.PostAsync(path + "/cancel", null);
            Equal("succeeded", (await cancelled.Content.ReadFromJsonAsync<TranslationTaskDto>())!.Status, "HTTP cancellation preserves previously completed work");
            using var extra = await client.PutAsJsonAsync("/v1/translations/" + Guid.NewGuid(), new { mode = "text", text = "x", targetLanguage = "zh", apiKey = "client-forged-key" });
            Equal(HttpStatusCode.BadRequest, extra.StatusCode, "HTTP rejects client-controlled provider configuration");
            string preCancelledPath = "/v1/translations/" + Guid.NewGuid();
            using var preCancel = await client.PostAsync(preCancelledPath + "/cancel", null);
            Equal(HttpStatusCode.OK, preCancel.StatusCode, "HTTP cancellation may precede the upload request");
            using var lateUpload = await client.PutAsJsonAsync(preCancelledPath, new { mode = "text", text = "late", targetLanguage = "zh" });
            Equal("cancelled", (await lateUpload.Content.ReadFromJsonAsync<TranslationTaskDto>())!.Status,
                "HTTP late upload receives its existing cancellation");
            HttpStatusCode lastStatus = HttpStatusCode.OK;
            for (int index = 0; index < 20; index++)
            {
                using var limited = await client.PutAsJsonAsync("/v1/translations/" + Guid.NewGuid(), new { mode = "text", text = "limit", targetLanguage = "zh" });
                lastStatus = limited.StatusCode;
            }
            Equal(HttpStatusCode.TooManyRequests, lastStatus, "Translation uploads use the submission rate policy");
            using var stillReadable = await client.GetAsync(path);
            Equal(HttpStatusCode.OK, stillReadable.StatusCode, "Upload limits do not prevent result recovery queries");
            using var stillCancellable = await client.PostAsync(path + "/cancel", null);
            Equal(HttpStatusCode.OK, stillCancellable.StatusCode, "Upload limits do not prevent task cancellation");
        }
        finally { await app.StopAsync(); }
    }

    private static TranslationService Service(Store store, ITranslationProcessor processor) => new(store, processor, new(), NullLogger<TranslationService>.Instance);
    private static TranslationInput TextInput(string text) => TranslationInput.Validate(new("text", text, null, "简体中文"));
    private static TranslationOptions Options() => new() { BaseUrl = "https://upstream.invalid/v1", ApiKey = "synthetic-server-key", Model = "server-only-model" };
    private static string ResultJson() => JsonSerializer.Serialize(new TranslationResult(false, "source", "译文"), TranslationInput.Json);
    private static string Envelope(string content, string finish = "stop") => JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = finish, message = new { content } } } });
    private static DefaultHttpContext Context(string token) { var context = new DefaultHttpContext(); context.Request.Headers.Authorization = "Bearer " + token; return context; }
    private static async Task<TranslationTaskDto> Terminal(TranslationService service, string user, Guid id)
    {
        for (int attempt = 0; attempt < 300; attempt++)
        { var job = service.Get(user, id); if (job.Status is not ("queued" or "running")) return job; await Task.Delay(10); }
        throw new Exception("Synthetic task did not terminate");
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
    private static void Equal<T>(T expected, T actual, string message) => Check(EqualityComparer<T>.Default.Equals(expected, actual), message + $"; expected={expected}, actual={actual}");
    private static void Throws(int status, Action action)
    { try { action(); } catch (ApiException error) when (error.StatusCode == status) { checks++; return; } throw new Exception("Expected API status " + status); }
    private static async Task ThrowsProcessing(Func<Task<TranslationResult>> action)
    { try { await action(); } catch (TranslationProcessingException) { checks++; return; } throw new Exception("Invalid upstream output was accepted"); }
    private static async Task ThrowsApiAsync(int status, Func<Task> action)
    { try { await action(); } catch (ApiException error) when (error.StatusCode == status) { checks++; return; } throw new Exception("Expected API status " + status); }

    private sealed record ClientTaskContract(Guid Id, string Status, string? SourceText, string? Translation, string? Error, long Balance);
    private sealed class FakePaymentProvider : IAfdianClient
    {
        public IReadOnlyList<VerifiedAfdianOrder> Orders { get; set; } = [];
        public Dictionary<int, IReadOnlyList<VerifiedAfdianOrder>>? PageOrders { get; init; }
        public List<string?> Requested { get; } = [];
        public List<int> RequestedPages { get; } = [];
        public Task<IReadOnlyList<VerifiedAfdianOrder>> QueryAsync(string? tradeNo, int page, CancellationToken token)
        {
            Requested.Add(tradeNo); RequestedPages.Add(page);
            return Task.FromResult(tradeNo is null && PageOrders is not null
                ? PageOrders.GetValueOrDefault(page, []) : Orders);
        }
    }

    private sealed class FakeProcessor : ITranslationProcessor
    {
        private int calls; public int Calls => calls; public bool IsAvailable => true;
        public Task<TranslationResult> ProcessAsync(TranslationInput input, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            return input.Text switch
            {
                "FAIL" => throw new TranslationProcessingException("synthetic_failure"),
                "NO_TEXT" => Task.FromResult(new TranslationResult(true, "", "")),
                "OVERSIZED" => Task.FromResult(new TranslationResult(false, "source", new string('x', 32_001))),
                _ => Task.FromResult(new TranslationResult(false, input.Text ?? "画像", "合成译文"))
            };
        }
    }
    private sealed class BlockingProcessor : ITranslationProcessor
    {
        public bool IsAvailable => true;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<TranslationResult> Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Returned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<TranslationResult> ProcessAsync(TranslationInput input, CancellationToken cancellationToken)
        { Started.TrySetResult(); var result = await Finish.Task; Returned.TrySetResult(); return result; }
    }
    private sealed class ReplyHandler(IEnumerable<string> responses) : HttpMessageHandler
    {
        private readonly Queue<string> replies = new(responses);
        public List<string> Bodies { get; } = []; public HashSet<string> Authorization { get; } = []; public List<string> Uris { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken)); Authorization.Add(request.Headers.Authorization!.ToString()); Uris.Add(request.RequestUri!.AbsoluteUri);
            return new(HttpStatusCode.OK) { Content = new StringContent(replies.Dequeue(), Encoding.UTF8, "application/json") };
        }
    }
    private sealed class Fixture : IDisposable
    {
        private readonly string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Mugonzei-server-tests-" + Guid.NewGuid().ToString("N"));
        public string Path { get; } public Store Store { get; }
        public Fixture() { Path = System.IO.Path.Combine(directory, "fixture.db"); Store = new(Path); }
        public void LegacyUser(Credentials input)
        {
            byte[] salt = RandomNumberGenerator.GetBytes(16);
            byte[] hash = Rfc2898DeriveBytes.Pbkdf2(input.Password, salt, 210_000, HashAlgorithmName.SHA256, 32);
            RedemptionSql(Path, "INSERT INTO users(id,username,password_hash,salt,iterations) VALUES($id,$name,$hash,$salt,210000)",
                ("$id", Guid.NewGuid().ToString("N")), ("$name", input.Username), ("$hash", hash), ("$salt", salt));
        }
        public string User(int credits = 0)
        {
            string user = Guid.NewGuid().ToString("N");
            RedemptionSql(Path, "INSERT INTO users(id,username,password_hash,salt,iterations) VALUES($id,$name,$hash,$salt,210000)",
                ("$id", user), ("$name", "fixture_" + user), ("$hash", RandomNumberGenerator.GetBytes(32)), ("$salt", RandomNumberGenerator.GetBytes(16)));
            if (credits > 0) Fund(user, credits); return user;
        }
        public void Fund(string user, int credits)
        {
            var order = Store.CreateOrder(user, Guid.NewGuid().ToString(), new("fixture", "Synthetic", credits, 100, "fixture-plan"));
            Store.CreditOrder(order.Id, "fixture_" + Guid.NewGuid().ToString("N"), order.AmountFen, order.AfdianPlanId, order.CustomId);
        }
        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            // Delete only these known fixture files; never enumerate or recursively delete paths.
            foreach (string suffix in new[] { "", "-wal", "-shm", ".lock" }) if (File.Exists(Path + suffix)) File.Delete(Path + suffix);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: false);
        }
    }
}
