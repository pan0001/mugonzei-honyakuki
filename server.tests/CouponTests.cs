using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mugonzei.Server;

internal static partial class Program
{
    private static async Task CouponTests()
    {
        using var fixture = new Fixture();
        string user = fixture.User(), other = fixture.User();
        var plan = new Plan("credits-200", "200 credits", 200, 500, new string('a', 32));
        var otherPlan = new Plan("credits-500", "500 credits", 500, 1000, new string('b', 32));
        string providerId = new('c', 32);
        Plan[] plans = [plan, otherPlan];
        var options = new ServerOptions { Plans = plans, CreatorId = "example", LivePayments = true, PaymentBaseUrl = "https://ifdian.net/order/create" };
        var gateway = new FakePaymentProvider();
        var service = new PaymentService(fixture.Store, options, gateway);
        var closed = new PaymentService(fixture.Store, new() { Plans = plans }, gateway);
        static string NewId() => Guid.NewGuid().ToString("D");

        // Rebuild the new side tables beside a populated pre-coupon schema.
        var legacy = fixture.Store.CreateOrder(user, NewId(), plan);
        RedemptionSql(fixture.Path, "DROP TABLE order_coupons; DROP TABLE coupons;");
        var upgraded = new Store(fixture.Path);
        Equal(plan.AmountFen, upgraded.GetOrder(user, legacy.Id).OriginalAmountFen, "Old orders retain their paid price as original");
        Equal(0, upgraded.GetOrders(user).Single().DiscountFen, "Old orders show no discount after migration");
        Equal(new PriceQuote(plan.Id, 200, 500, 500), service.Quote(plan.Id), "Ordinary prices keep existing behavior");
        Throws(503, () => closed.Quote(plan.Id, "WELCOME"));
        Throws(503, () => closed.CreateOrder(user, NewId(), plan.Id, "WELCOME", 390));
        foreach (int price in new[] { 0, -1, 500, 501 })
            Throws(400, () => upgraded.CreateCoupon(plans, plan.Id, price, providerId));
        foreach (string bad in new[] { plan.AfdianPlanId.ToUpperInvariant(), otherPlan.AfdianPlanId, "", "not-a-plan", new string('z', 32) })
            Throws(400, () => upgraded.CreateCoupon(plans, plan.Id, 390, bad));
        foreach (string bad in new[] { "贺岁优惠", "MUspecial", "A-B", "A_BC", "-SAVE10", "HEL--LO", " SAVE10", new string('x', 81) })
            Throws(400, () => upgraded.CreateCoupon(plans, plan.Id, 390, providerId, customCode: bad));
        Throws(400, () => upgraded.CreateCoupon(plans, plan.Id, 390, providerId, DateTimeOffset.UtcNow.AddDays(-1)));

        var issued = upgraded.CreateCoupon(plans, plan.Id, 390, providerId.ToUpperInvariant(), DateTimeOffset.UtcNow.AddDays(2), "Welcome");
        Equal("WELCOME", issued.Code, "Operator receives the canonical display code once");
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes("WELCOME")));
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = fixture.Path }.ToString()))
        {
            db.Open(); using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT code_hash FROM coupons WHERE id=$id"; cmd.Parameters.AddWithValue("$id", issued.Id);
            Equal(hash, (string?)cmd.ExecuteScalar(), "Storage uses the normalized lookup hash");
        }
        string listing = JsonSerializer.Serialize(upgraded.ListCoupons());
        Check(!listing.Contains(issued.Code, StringComparison.OrdinalIgnoreCase) && !listing.Contains(hash, StringComparison.OrdinalIgnoreCase),
            "Operator listing omits the coupon code and its hash");
        Throws(409, () => upgraded.CreateCoupon(plans, plan.Id, 390, providerId, customCode: "WE-LCOME"));
        Throws(409, () => upgraded.CreateCoupon(plans, plan.Id, 391, providerId));
        Throws(409, () => upgraded.CreateCoupon(plans, otherPlan.Id, 390, providerId));
        var second = upgraded.CreateCoupon(plans, plan.Id, 390, providerId);
        Check(second.Code.StartsWith("MC-") && second.Code.Length == 35, "Automatically generated campaign codes contain 128 random bits");
        Throws(409, () => upgraded.CreateCoupon([otherPlan], otherPlan.Id, 900, plan.AfdianPlanId));
        _ = upgraded.CreateCoupon(plans, otherPlan.Id, 900, new string('d', 32));
        Throws(409, () => upgraded.CreateCoupon([plan], plan.Id, 390, otherPlan.AfdianPlanId));

        var quote = service.Quote(plan.Id, "  wel-co me ");
        Equal(new PriceQuote(plan.Id, 200, 500, 390), quote, "Only the payment amount changes; original price and credits are server-authoritative");
        Equal(110, quote.DiscountFen, "Discount is computed exactly in integer fen");
        Throws(400, () => service.Quote(otherPlan.Id, issued.Code));
        foreach (string bad in new[] { "UNKNOWN", "", " ", "MU-1234-AABB", "WELＣOME", new string('x', 81) })
            Throws(400, () => service.Quote(plan.Id, bad));
        foreach (int? wrong in new int?[] { null, -1, 1, 389, 391, 500 })
            Throws(409, () => service.CreateOrder(user, NewId(), plan.Id, issued.Code, wrong));
        Equal(1, upgraded.GetOrders(user).Count, "Rejected quote confirmations create no orders");
        // Simulate a failure between the order insert and its coupon association: either both commit or neither does.
        RedemptionSql(fixture.Path, "CREATE TRIGGER reject_coupon_fixture BEFORE INSERT ON order_coupons BEGIN SELECT RAISE(ABORT,'fixture'); END;");
        try
        {
            try { service.CreateOrder(user, NewId(), plan.Id, issued.Code, 390); throw new Exception("Expected synthetic constraint failure"); }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19) { }
            Equal(1, upgraded.GetOrders(user).Count, "Failed coupon association leaves no partial discounted order");
        }
        finally { RedemptionSql(fixture.Path, "DROP TRIGGER reject_coupon_fixture;"); }

        string requestId = NewId();
        var orders = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() =>
            service.CreateOrder(user, requestId, plan.Id, "WeLcOmE", quote.AmountFen))));
        var order = orders[0];
        Check(orders.All(o => o == order), "Concurrent identical requests snapshot one coupon order");
        Equal(providerId, order.AfdianPlanId, "The order contains the real discounted provider plan");
        Equal(390, order.AmountFen, "Confirmed order stores amount paid");
        Equal(500, order.OriginalAmountFen, "Confirmed order stores original amount");
        Equal(order, new Store(fixture.Path).GetOrder(user, order.Id), "Complete discount snapshot survives restart");
        var checkout = options.BuildPayUrl(order);
        Check(checkout.Contains("plan_id=" + providerId) && checkout.Contains("custom_order_id=" + order.CustomId) &&
            !checkout.Contains("custom_price") && !checkout.Contains(issued.Code), "Checkout selects the discounted plan without overriding provider price");
        Throws(409, () => service.CreateOrder(user, requestId, plan.Id));
        Throws(409, () => service.CreateOrder(user, requestId, plan.Id, second.Code, 390));
        Throws(409, () => service.CreateOrder(user, requestId, plan.Id, issued.Code, 500));
        Throws(409, () => service.CreateOrder(user, legacy.RequestId, plan.Id, issued.Code, 390));
        var otherOrder = service.CreateOrder(other, requestId, plan.Id, issued.Code, quote.AmountFen);
        Check(otherOrder.UserId == other && otherOrder.Id != order.Id, "Reusable campaigns give independent checkouts to different accounts");

        foreach (var change in new[] { plan with { AmountFen = 501 }, plan with { Credits = 500 }, plan with { AfdianPlanId = new string('e', 32) } })
        {
            var changed = new PaymentService(upgraded, new() { MockPayments = true, Plans = [change] }, gateway);
            Throws(400, () => changed.Quote(plan.Id, issued.Code));
        }
        var overlap = new PaymentService(upgraded, new() { MockPayments = true, Plans = [plan, otherPlan with { AfdianPlanId = providerId }] }, gateway);
        Throws(400, () => overlap.CreateOrder(user, NewId(), plan.Id, issued.Code, 390));
        Check(upgraded.RevokeCoupon(issued.Id) && !upgraded.RevokeCoupon(issued.Id), "Revocation is repeatable");
        Check(upgraded.ListCoupons().Single(c => c.Id == issued.Id).RevokedAt is not null, "Operator sees the campaign has closed");
        Throws(400, () => service.Quote(plan.Id, issued.Code));
        Throws(400, () => service.CreateOrder(user, NewId(), plan.Id, issued.Code, 390));
        Throws(409, () => upgraded.CreateCoupon(plans, plan.Id, 390, providerId, customCode: "WELCOME"));
        Throws(409, () => upgraded.CreateCoupon(plans, plan.Id, 350, new string('f', 32), customCode: "WELCOME"));
        var reversePlan = otherPlan with { AfdianPlanId = providerId.ToUpperInvariant() };
        var reverse = new PaymentService(upgraded, new() { MockPayments = true, Plans = [reversePlan] }, gateway);
        Throws(409, () => reverse.Quote(otherPlan.Id));
        Throws(409, () => reverse.CreateOrder(other, NewId(), otherPlan.Id));
        Throws(409, () => upgraded.CreateCoupon([reversePlan], otherPlan.Id, 800, new string('e', 32)));
        var removedPlan = new PaymentService(upgraded, new() { MockPayments = true, Plans = [] }, gateway);
        Equal(order, removedPlan.CreateOrder(user, requestId, plan.Id, issued.Code, 390), "Identical retry keeps snapshot even after revocation and plan removal");
        RedemptionSql(fixture.Path, "UPDATE coupons SET expires_at=$when WHERE id=$id",
            ("$when", DateTimeOffset.UtcNow.AddHours(-1).ToString("O", CultureInfo.InvariantCulture)), ("$id", second.Id));
        Throws(400, () => service.CreateOrder(user, NewId(), plan.Id, second.Code, 390));

        var verified = new VerifiedAfdianOrder("trade-fixture", order.CustomId, providerId, 390, 2);
        foreach (var wrong in new[] { verified with { PlanId = plan.AfdianPlanId }, verified with { AmountFen = 500 }, verified with { AmountFen = 389 } })
            Throws(409, () => service.ApplyVerified(wrong));
        Check(service.ApplyVerified(verified with { CustomId = "unrelated" }) is null &&
            service.ApplyVerified(verified with { Status = 1 }) is null, "Unrelated and unpaid notifications cannot credit");
        Equal(0L, upgraded.GetAccount(user).Balance, "Invalid claims and mere coupons add no credits");
        gateway.Orders = [verified];
        await service.VerifyNotificationAsync(verified.TradeNo, default);
        await service.VerifyNotificationAsync(verified.TradeNo, default);
        Equal(300L, upgraded.GetAccount(user).Balance, "The verified discounted first recharge grants the base and bonus once, even after coupon revocation");
        Equal(1, upgraded.GetLedger(user).Count, "Provider retries produce one ledger entry");
        Throws(409, () => service.ApplyVerified(verified with { CustomId = otherOrder.CustomId }));
        Equal(0L, upgraded.GetAccount(other).Balance, "Provider transaction cannot transfer between users");
        RedemptionSql(fixture.Path, "UPDATE users SET balance=$value WHERE id=$user", ("$value", long.MaxValue - 1), ("$user", other));
        Throws(409, () => service.ApplyVerified(verified with { TradeNo = "other-fixture", CustomId = otherOrder.CustomId }));
        Equal("pending", upgraded.GetOrder(other, otherOrder.Id).Status, "Balance overflow rolls back order payment and permits later reconciliation");
        Equal(0, upgraded.GetLedger(other).Count, "Overflow leaves no partial credit or ledger record");
    }
}
