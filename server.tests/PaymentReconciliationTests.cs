using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Mugonzei.Server;

internal static partial class Program
{
    private static async Task DurablePaymentVerification()
    {
        using var fixture = new Fixture();
        var clock = new RegistrationClock();
        var provider = new FakePaymentProvider();
        var plan = new Plan("credits-200", "Synthetic", 200, 500, "fixture-provider");
        var options = new ServerOptions { LivePayments = true, Plans = [plan] };
        var payments = new PaymentService(fixture.Store, options, provider, clock);
        var worker = new PaymentReconciliationWorker(fixture.Store, payments, clock, NullLogger<PaymentReconciliationWorker>.Instance);
        string user = fixture.User(), trade = "synthetic-late";
        var order = payments.CreateOrder(user, Guid.NewGuid().ToString(), plan.Id);
        payments.AcceptNotification(trade);
        payments.AcceptNotification(trade);
        Check(provider.Requested.Count == 0, "Webhook acknowledgement persists work without a provider call");
        Check(await worker.ProcessNextAsync(default) && !await worker.ProcessNextAsync(default), "Duplicate notifications share one scheduled query");
        var verified = new VerifiedAfdianOrder(trade, order.CustomId, plan.AfdianPlanId, plan.AmountFen, 2);
        provider.Orders = [verified with { Status = 1 }, verified with { TradeNo = "unrelated" }];
        clock.Advance(TimeSpan.FromSeconds(4));
        payments.AcceptNotification(trade);
        Check(!await worker.ProcessNextAsync(default), "Replayed notifications cannot advance the next check");
        clock.Advance(TimeSpan.FromSeconds(1));
        Check(await worker.ProcessNextAsync(default) && fixture.Store.GetAccount(user).Balance == 0, "Pending or unrelated signed records never credit an order");

        var reopened = new Store(fixture.Path);
        var restoredPayments = new PaymentService(reopened, options, provider, clock);
        var restarted = new PaymentReconciliationWorker(reopened, restoredPayments, clock, NullLogger<PaymentReconciliationWorker>.Instance);
        clock.Advance(TimeSpan.FromSeconds(14));
        Check(!await restarted.ProcessNextAsync(default), "Retry backoff survives service restart");
        clock.Advance(TimeSpan.FromSeconds(1));
        provider.Orders = [verified];
        Check(await restarted.ProcessNextAsync(default), "A later verified result settles without another provider notification");
        restoredPayments.AcceptNotification(trade);
        Check(reopened.GetAccount(user).Balance == 300 && reopened.GetLedger(user).Count == 1
            && !await restarted.ProcessNextAsync(default) && provider.Requested.Count == 3,
            "A settled trade replay neither credits again nor consumes a provider query");
        var refund = reopened.HoldRefund(order.Id, Guid.NewGuid().ToString());
        Equal("refund_pending", (await restoredPayments.RefreshAsync(user, order.Id, default)).Status, "Refund holds return locally on refresh");
        reopened.ResolveRefund(refund.Id, true);
        Equal("refunded", (await restoredPayments.RefreshAsync(user, order.Id, default)).Status, "Refunded orders return locally on refresh");
        restoredPayments.AcceptNotification(trade);
        Check(!await restarted.ProcessNextAsync(default) && provider.Requested.Count == 3 && reopened.GetAccount(user).Balance == 0,
            "Refunded trade notifications cannot regain credits or reopen verification");

        // Crash after claim: another verifier cannot take the live lease, and its eventual
        // second claim is fenced against delayed writes from the abandoned first attempt.
        restoredPayments.AcceptNotification("lease-fixture");
        var abandoned = reopened.ClaimPaymentCheck(clock.GetUtcNow())!;
        Check(abandoned.Attempt == 1 && reopened.ClaimPaymentCheck(clock.GetUtcNow()) is null, "A claimed job has one active lease");
        clock.Advance(TimeSpan.FromMinutes(1));
        var lease = reopened.ClaimPaymentCheck(clock.GetUtcNow())!;
        reopened.FinishPaymentCheck(abandoned, null);
        reopened.FinishPaymentCheck(lease, clock.GetUtcNow().AddSeconds(10));
        clock.Advance(TimeSpan.FromSeconds(11));
        restoredPayments.AcceptNotification("newer-fixture");
        var earlier = reopened.ClaimPaymentCheck(clock.GetUtcNow())!;
        Equal("lease-fixture", earlier.TradeNo, "New traffic cannot jump ahead of an overdue retry");
        Equal(3, earlier.Attempt, "Stale completion cannot remove the recovered job");

        using var transient = new Fixture();
        var unreliableGateway = new TemporarilyUnavailableGateway();
        var recovering = new PaymentService(transient.Store, options, unreliableGateway, clock);
        var recoveryWorker = new PaymentReconciliationWorker(transient.Store, recovering, clock, NullLogger<PaymentReconciliationWorker>.Instance);
        string another = transient.User();
        var delayed = recovering.CreateOrder(another, Guid.NewGuid().ToString(), plan.Id);
        recovering.AcceptNotification("provider-timeout-test");
        Check(await recoveryWorker.ProcessNextAsync(default) && !await recoveryWorker.ProcessNextAsync(default),
            "Unavailable signed API schedules a delayed retry");
        clock.Advance(TimeSpan.FromSeconds(5));
        unreliableGateway.Record = new("provider-timeout-test", delayed.CustomId, plan.AfdianPlanId, plan.AmountFen + 1, 2);
        Check(await recoveryWorker.ProcessNextAsync(default) && transient.Store.GetAccount(another).Balance == 0,
            "A mismatched confirmed payment is never credited");
        clock.Advance(TimeSpan.FromHours(2));
        Check(!await recoveryWorker.ProcessNextAsync(default), "A verified price mismatch does not keep querying automatically");

        using var capped = new Fixture();
        var limited = new PaymentService(capped.Store, options, provider, clock);
        Throws(400, () => limited.AcceptNotification("invalid/trade"));
        Throws(503, () => new PaymentService(capped.Store, new ServerOptions(), provider, clock).AcceptNotification("disabled"));
        for (int i = 0; i < Store.PaymentCheckCapacity; i++) limited.AcceptNotification("bounded-" + i);
        Throws(503, () => limited.AcceptNotification("full"));
        limited.AcceptNotification("bounded-1"); // duplicates still acknowledged while full
        clock.Advance(TimeSpan.FromDays(1));
        Check(capped.Store.ClaimPaymentCheck(clock.GetUtcNow()) is null, "Unmatched notifications expire without another provider query");
        limited.AcceptNotification("fresh");
        Equal("fresh", capped.Store.ClaimPaymentCheck(clock.GetUtcNow())!.TradeNo, "Expired entries release queue capacity");

        await PaymentWebhookRestart();
    }

    private static async Task PaymentWebhookRestart()
    {
        using var fixture = new Fixture();
        var clock = new RegistrationClock();
        var provider = new FakePaymentProvider();
        var plan = new Plan("synthetic-webhook", "Fixture", 3, 175, "fixture");
        var options = new ServerOptions { LivePayments = true, Plans = [plan] };
        var payments = new PaymentService(fixture.Store, options, provider, clock);
        string user = fixture.User();
        var order = payments.CreateOrder(user, Guid.NewGuid().ToString(), plan.Id);
        var ready = new TaskCompletionSource<(Uri, IHostApplicationLifetime)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = MugonzeiHost.RunAsync(["--environment","Development","--urls","http://127.0.0.1:0",
            "--MUGONZEI_DATABASE", fixture.Path, "--MUGONZEI_LIVE_PAYMENTS","true","--MUGONZEI_MOCK_PAYMENTS","false",
            "--AFDIAN_USER_ID","fixture","--AFDIAN_TOKEN","fixture","--AFDIAN_PAYMENT_BASE_URL","https://afdian.com/order/create",
            "--MUGONZEI_PLANS_JSON",JsonSerializer.Serialize(new[] { plan }),"--MUGONZEI_TRUSTED_PROXIES","",
            "--Translation:Mock","false","--Translation:BaseUrl","","--Translation:ApiKey","","--Logging:LogLevel:Default","Warning"],
            services =>
            {
                services.RemoveAll<IAfdianClient>();
                services.AddSingleton<IAfdianClient>(provider);
                services.AddSingleton<TimeProvider>(clock);
                services.AddSingleton<IHostedService>(sp => new CheckoutTestAddress(sp.GetRequiredService<IHostApplicationLifetime>(),
                    sp.GetRequiredService<IServer>(), ready));
            });
        if (await Task.WhenAny(host, ready.Task) == host) { await host; throw new Exception("Payment test server did not start"); }
        var (address, lifetime) = await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            using var http = new HttpClient { BaseAddress = address, Timeout = TimeSpan.FromSeconds(5) };
            var forged = new { data = new { type = "order", order = new { out_trade_no = "restart-trade", plan_id = plan.AfdianPlanId,
                custom_order_id = order.CustomId, status = 2, total_amount = "1.75" } } };
            using var response = await http.PostAsJsonAsync("/v1/payments/afdian/webhook", forged);
            var receipt = await response.Content.ReadFromJsonAsync<JsonElement>();
            Check(response.IsSuccessStatusCode && receipt.GetProperty("ec").GetInt32() == 200 && fixture.Store.GetAccount(user).Balance == 0,
                "HTTP callback acknowledges a queued number but ignores even plausible receipt details from the request body");
        }
        finally { lifetime.StopApplication(); await host.WaitAsync(TimeSpan.FromSeconds(10)); }

        // After restart the queued number still exists whether or not the old host polled it.
        clock.Advance(TimeSpan.FromMinutes(2));
        provider.Orders = [new("restart-trade", order.CustomId, plan.AfdianPlanId, plan.AmountFen, 2)];
        var reopened = new Store(fixture.Path);
        var newService = new PaymentService(reopened, options, provider, clock);
        var worker = new PaymentReconciliationWorker(reopened, newService, clock, NullLogger<PaymentReconciliationWorker>.Instance);
        Check(await worker.ProcessNextAsync(default) && reopened.GetAccount(user).Balance == 3,
            "A callback acknowledged before shutdown can settle from an independent signed query after restart");
    }

    private sealed class TemporarilyUnavailableGateway : IAfdianClient
    {
        internal VerifiedAfdianOrder? Record { get; set; }
        public Task<IReadOnlyList<VerifiedAfdianOrder>> QueryAsync(string? tradeNo, int page, CancellationToken token) =>
            Record is null ? throw new ApiException(502, "payment_verification_unavailable", "Synthetic provider outage")
            : Task.FromResult<IReadOnlyList<VerifiedAfdianOrder>>([Record]);
    }
}
