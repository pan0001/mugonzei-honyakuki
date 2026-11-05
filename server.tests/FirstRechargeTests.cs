using Microsoft.Data.Sqlite;
using Mugonzei.Server;

internal static partial class Program
{
    private static readonly Plan SmallRecharge = new("credits-200", "Small", 200, 500, new string('a', 32));
    private static readonly Plan LargeRecharge = new("credits-500", "Large", 500, 1000, new string('b', 32));
    private static OrderRecord PlaceRecharge(Store store, string user, Plan plan) =>
        store.CreateOrder(user, Guid.NewGuid().ToString(), plan);
    private static OrderRecord SettleRecharge(Store store, OrderRecord order) =>
        store.CreditOrder(order.Id, "test-" + order.Id, order.AmountFen, order.AfdianPlanId, order.CustomId);

    private static async Task FirstRechargeAccounting()
    {
        using var fixture = new Fixture();
        Store store = fixture.Store;
        string user = fixture.User();
        var code = store.CreateRedemptionCodes(9, 1, null, "fixture").Single();
        store.RedeemCode(user, code.Code);
        var earlier = PlaceRecharge(store, user, SmallRecharge);
        var later = PlaceRecharge(store, user, LargeRecharge);
        Check(store.GetAccount(user).FirstRechargeEligible && earlier.FirstRechargeBonusCredits == 0 && later.FirstRechargeBonusCredits == 0,
            "Redeemed credits and pending orders do not consume or reserve the first recharge");
        var provider = new FakePaymentProvider { PageOrders = new() { [2] = [new("test-" + later.Id, later.CustomId, later.AfdianPlanId, later.AmountFen, 2)] } };
        var payments = new PaymentService(store, new ServerOptions { LivePayments = true, Plans = [SmallRecharge, LargeRecharge] }, provider);
        var paid = await payments.RefreshAsync(user, later.Id, default);
        Check(paid.Credits == 600 && paid.BaseCredits == 500 && paid.FirstRechargeBonusCredits == 100
            && !store.GetAccount(user).FirstRechargeEligible, "A later-created large order can settle first through missed-webhook reconciliation");
        SettleRecharge(store, earlier);
        Check(store.GetAccount(user).Balance == 809 && store.GetOrder(user, earlier.Id).Credits == 200,
            "The other pending order grants only its base credits after the large first recharge");
        var refund = store.HoldRefund(paid.Id, Guid.NewGuid().ToString());
        Check(refund.Credits == 600 && store.GetAccount(user).Reserved == 600 && !store.GetAccount(user).FirstRechargeEligible,
            "A refund reserves the complete award and keeps historical eligibility consumed");
        store.ResolveRefund(refund.Id, true);
        SettleRecharge(store, later);
        Equal(209L, store.GetAccount(user).Balance, "A confirmed refund reverses the whole award and late payment notifications cannot restore it");
        var repeat = SettleRecharge(store, PlaceRecharge(store, user, LargeRecharge));
        Check(repeat.Credits == 500 && repeat.FirstRechargeBonusCredits == 0, "Refunds do not reset the cross-plan first-recharge benefit");

        string racing = fixture.User();
        var one = PlaceRecharge(store, racing, SmallRecharge);
        var two = PlaceRecharge(store, racing, LargeRecharge);
        var otherProcess = new Store(fixture.Path);
        using var gate = new ManualResetEventSlim();
        var runs = Enumerable.Range(0, 8).Select(i => Task.Run(() => {
            gate.Wait();
            return SettleRecharge(i % 3 == 0 ? otherProcess : store, i % 2 == 0 ? one : two);
        })).ToArray();
        gate.Set();
        await Task.WhenAll(runs);
        var restarted = new Store(fixture.Path);
        var orders = restarted.GetOrders(racing);
        Check(orders.Count == 2 && orders.Sum(o => o.Credits) == 800 && orders.Count(o => o.FirstRechargeBonusCredits == 100) == 1
            && orders.Sum(o => o.BaseCredits) == 700, "Concurrent settlements and duplicate callbacks across both tiers grant precisely one bonus");
        Check(restarted.GetAccount(racing).Balance == 800 && restarted.GetLedger(racing).Count == 2
            && restarted.GetLedger(racing).Sum(row => row.Amount) == 800, "After restart, both paid orders and their ledger entries agree with the balance");
        FirstRechargeFailures();
        HistoricalRechargeEligibility();
    }

    private static void FirstRechargeFailures()
    {
        using var fixture = new Fixture();
        var store = fixture.Store;
        string user = fixture.User();
        var small = PlaceRecharge(store, user, SmallRecharge);
        Throws(409, () => store.CreditOrder(small.Id, "wrong-payment", 499, small.AfdianPlanId, small.CustomId));
        RedemptionSql(fixture.Path, "UPDATE users SET balance=$balance WHERE id=$id", ("$balance", long.MaxValue - 200), ("$id", user));
        Throws(409, () => SettleRecharge(store, small));
        Check(store.GetAccount(user).FirstRechargeEligible && store.GetOrder(user, small.Id).Status == "pending" && store.GetLedger(user).Count == 0,
            "If the base fits but the bonus would overflow, neither is posted and eligibility remains");
        RedemptionSql(fixture.Path, "UPDATE users SET balance=0 WHERE id=$id", ("$id", user));
        RedemptionSql(fixture.Path, "CREATE TRIGGER fail_recharge_ledger BEFORE INSERT ON ledger BEGIN SELECT RAISE(ABORT,'test failure'); END;");
        try { SettleRecharge(store, small); throw new Exception("Expected injected ledger failure"); }
        catch (SqliteException error) when (error.SqliteErrorCode == 19) { checks++; }
        finally { RedemptionSql(fixture.Path, "DROP TRIGGER fail_recharge_ledger;"); }
        var paid = SettleRecharge(new Store(fixture.Path), small);
        Check(paid.Credits == 300 && paid.FirstRechargeBonusCredits == 100 && store.GetLedger(user).Single().Amount == 300,
            "Rolling back an award also rolls back its unique bonus record and allows a safe retry");
        string job = Guid.NewGuid().ToString();
        store.ReserveTranslation(user, job, "fixture-hash", "{}");
        Throws(409, () => store.HoldRefund(small.Id, Guid.NewGuid().ToString()));
        store.CancelTranslation(user, job);
        var hold = store.HoldRefund(small.Id, Guid.NewGuid().ToString());
        store.ResolveRefund(hold.Id, false);
        Check(store.GetAccount(user).Balance == 300 && store.GetAccount(user).Reserved == 0 && !store.GetAccount(user).FirstRechargeEligible,
            "A cancelled refund restores all available credits but never a second bonus");
        foreach (var other in new[] { SmallRecharge with { Id = "other" }, SmallRecharge with { AmountFen = 501 }, LargeRecharge with { Credits = 501 } })
        {
            string customer = fixture.User();
            var paidOther = SettleRecharge(store, PlaceRecharge(store, customer, other));
            Check(paidOther.FirstRechargeBonusCredits == 0 && paidOther.Credits == other.Credits && !store.GetAccount(customer).FirstRechargeEligible,
                "Only the two precise official plan snapshots qualify, and any successful prior recharge counts");
        }
    }

    private static void HistoricalRechargeEligibility()
    {
        using var fixture = new Fixture();
        var store = fixture.Store;
        var legacy = new List<(string User, OrderRecord Order, AccountRecord Account, IReadOnlyList<LedgerRecord> Ledger)>();
        foreach (string status in new[] { "paid", "refund_pending", "refunded" })
        {
            string user = fixture.User();
            var order = PlaceRecharge(store, user, SmallRecharge);
            // Reproduce the rows written by the previous release, which posted 200 credits.
            RedemptionSql(fixture.Path, """
                BEGIN IMMEDIATE;
                UPDATE orders SET status='paid',provider_trade_no=$trade WHERE id=$id;
                UPDATE users SET balance=200 WHERE id=$user;
                INSERT INTO ledger(user_id,kind,amount,balance,created_at,reference) VALUES($user,'recharge',200,200,$time,'payment:'||$trade);
                COMMIT;
                """, ("$trade", "legacy-" + order.Id), ("$id", order.Id), ("$user", user), ("$time", DateTimeOffset.UtcNow.ToString("O")));
            if (status != "paid")
            {
                var hold = store.HoldRefund(order.Id, Guid.NewGuid().ToString());
                if (status == "refunded") store.ResolveRefund(hold.Id, true);
            }
            legacy.Add((user, store.GetOrder(user, order.Id), store.GetAccount(user), store.GetLedger(user)));
        }
        string newBuyer = fixture.User();
        var pending = PlaceRecharge(store, newBuyer, LargeRecharge);
        RedemptionSql(fixture.Path, "DROP TABLE first_recharge_bonuses; DROP INDEX orders_successful_recharge_user;");
        var reopened = new Store(fixture.Path);
        foreach (var old in legacy)
        {
            Check(old.Order == reopened.GetOrder(old.User, old.Order.Id) && old.Order.FirstRechargeBonusCredits == 0
                && old.Account == reopened.GetAccount(old.User) && old.Ledger.SequenceEqual(reopened.GetLedger(old.User)),
                "Upgrading leaves historical paid and refunded orders, balances, reservations and ledgers intact");
            Check(!reopened.GetAccount(old.User).FirstRechargeEligible && SettleRecharge(reopened, PlaceRecharge(reopened, old.User, SmallRecharge)).Credits == 200,
                "A historical paid, refund-pending or refunded order permanently consumes first-recharge eligibility");
        }
        Check(reopened.GetAccount(newBuyer).FirstRechargeEligible && SettleRecharge(reopened, pending).Credits == 600,
            "An unconfirmed pre-upgrade order can still win the initial bonus when it first settles after deployment");
    }
}
