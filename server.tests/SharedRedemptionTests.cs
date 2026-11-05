using Microsoft.Data.Sqlite;
using Mugonzei.Server;

internal static partial class Program
{
    private static async Task SharedRedemptionTests()
    {
        SharedLegacyPromotion();
        await SharedConcurrentClaims();
        SharedFailureRecovery();
    }

    private static void SharedLegacyPromotion()
    {
        using var fixture = new Fixture();
        string owner = fixture.User(11), newcomer = fixture.User(), late = fixture.User();
        var batch = fixture.Store.CreateRedemptionCodes(7, 3, DateTimeOffset.UtcNow.AddDays(3), "legacy fixture");
        var used = batch[0];
        string reference = "redemption:" + used.Id;
        // Seed the exact rows that an older service wrote before per-account claims existed.
        RedemptionSql(fixture.Path, """
            BEGIN IMMEDIATE;
            UPDATE redemption_codes SET redeemed_by=$user, redeemed_at=$time WHERE id=$id;
            UPDATE users SET balance=balance+7 WHERE id=$user;
            INSERT INTO ledger(user_id,kind,amount,balance,created_at,reference)
                SELECT id,'redemption',7,balance,$time,$reference FROM users WHERE id=$user;
            COMMIT;
            """, ("$user", owner), ("$time", DateTimeOffset.UtcNow.AddHours(-1).ToString("O")), ("$id", used.Id), ("$reference", reference));
        var oldLedger = fixture.Store.GetLedger(owner);
        RedemptionSql(fixture.Path, "DROP TABLE redemption_claims; DROP TABLE shared_redemption_codes;");
        var migrated = new Store(fixture.Path);
        var untouched = migrated.ListRedemptionCodes().Single(x => x.Id == used.Id);
        Check(untouched.Mode == "singleUse" && untouched.ClaimCount == 1 && untouched.Status == "redeemed",
            "Opening an old database does not promote its consumed single-use codes");
        var promoted = migrated.ShareRedemptionCode(used.Id);
        Check(promoted.Mode == "oncePerAccount" && promoted.ClaimCount == 1 && promoted.Status == "active"
            && promoted.ExpiresAt == used.ExpiresAt && promoted.Credits == used.Credits,
            "Explicit promotion preserves the existing award, denomination and expiry");
        Equal(1L, migrated.ShareRedemptionCode(used.Id).ClaimCount, "Promotion retries preserve the original claimant");
        var originalRetry = new Store(fixture.Path).RedeemCode(owner, used.Code);
        Check(originalRetry.AlreadyRedeemed && originalRetry.Reference == reference && originalRetry.Balance == 18
            && migrated.GetLedger(owner).SequenceEqual(oldLedger), "Promotion and restart preserve the original ledger and balance exactly");
        Equal(7L, migrated.RedeemCode(newcomer, used.Code).Balance, "A second account can claim a promoted code");
        var stopped = migrated.RevokeRedemptionCode(used.Id);
        Check(stopped.Status == "revoked" && stopped.ClaimCount == 2, "Stopping a shared code preserves the claim count");
        Equal("revoked", migrated.ShareRedemptionCode(used.Id).Status, "Repeating promotion cannot reactivate a stopped shared code");
        var reopened = new Store(fixture.Path);
        RedemptionThrows(400, "redemption_unavailable", () => reopened.RedeemCode(late, used.Code));
        Check(reopened.RedeemCode(newcomer, used.Code).AlreadyRedeemed && reopened.RedeemCode(owner, used.Code).Reference == reference
            && reopened.GetLedger(owner).SequenceEqual(oldLedger) && reopened.GetLedger(newcomer).Single().Amount == 7,
            "Stopping an award blocks new claimants and preserves prior balances and history after restart");

        var unused = reopened.ShareRedemptionCode(batch[1].Id);
        Check(unused.Mode == "oncePerAccount" && unused.ClaimCount == 0, "An unclaimed single-use code can also be promoted");
        var other = reopened.ListRedemptionCodes().Single(x => x.Id == batch[2].Id);
        Check(other.Mode == "singleUse" && other.ClaimCount == 0, "An unrelated single-use code in the same batch is untouched");
        reopened.RedeemCode(owner, batch[2].Code);
        RedemptionThrows(400, "redemption_unavailable", () => reopened.RedeemCode(late, batch[2].Code));

        // An older binary uses this predicate to acquire the entire code. Shared codes must be closed to it.
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = fixture.Path }.ToString());
        db.Open();
        using var guard = db.CreateCommand();
        guard.CommandText = "SELECT count(*) FROM redemption_codes WHERE id=$id AND redeemed_by IS NULL AND revoked_at IS NULL";
        guard.Parameters.AddWithValue("$id", unused.Id);
        Equal(0L, (long)guard.ExecuteScalar()!, "A legacy service cannot start a single-use claim on an active shared code");
    }

    private static async Task SharedConcurrentClaims()
    {
        using var fixture = new Fixture();
        string first = fixture.User(), second = fixture.User(), late = fixture.User();
        var code = fixture.Store.CreateRedemptionCodes(4, 1, null, "parallel claims").Single();
        var alternate = new Store(fixture.Path);
        using var start = new ManualResetEventSlim();
        var promote = Task.Run(() => { start.Wait(); alternate.ShareRedemptionCode(code.Id); });
        var claim = Task.Run(() => { start.Wait(); return fixture.Store.RedeemCode(first, code.Code); });
        start.Set();
        await Task.WhenAll(promote, claim);
        Check(!(await claim).AlreadyRedeemed && alternate.ShareRedemptionCode(code.Id).ClaimCount == 1,
            "A claim racing promotion is retained once regardless of write order");

        var attempts = await Task.WhenAll(Enumerable.Range(0, 10).Select(i => Task.Run(() =>
            (i % 2 == 0 ? fixture.Store : alternate).RedeemCode(i % 3 == 0 ? first : second, code.Code))));
        Equal(1, attempts.Count(x => !x.AlreadyRedeemed), "Concurrent retries by two users add only the new user's claim");
        foreach (string user in new[] { first, second })
            Check(alternate.GetAccount(user).Balance == 4 && alternate.GetLedger(user).Single().Amount == 4,
                "Each concurrent claimant receives one credit award and ledger entry");
        RedemptionSql(fixture.Path, "UPDATE redemption_codes SET expires_at=$expiry WHERE id=$id",
            ("$expiry", DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O")), ("$id", code.Id));
        var expired = new Store(fixture.Path);
        var row = expired.ListRedemptionCodes().Single();
        Check(row.Status == "expired" && row.ClaimCount == 2, "Expiry preserves both durable claims");
        RedemptionThrows(400, "redemption_unavailable", () => expired.RedeemCode(late, code.Code));
        Check(expired.RedeemCode(first, code.Code).AlreadyRedeemed && expired.RedeemCode(second, code.Code).AlreadyRedeemed,
            "Both original claimants recover the same result after expiry and restart");
    }

    private static void SharedFailureRecovery()
    {
        using var fixture = new Fixture();
        string owner = fixture.User(), second = fixture.User();
        var code = fixture.Store.CreateRedemptionCodes(6, 1, null, "rollback").Single();
        fixture.Store.RedeemCode(owner, code.Code);
        RedemptionSql(fixture.Path, """
            CREATE TRIGGER fixture_abort_promote BEFORE UPDATE ON redemption_codes
            BEGIN SELECT RAISE(ABORT,'fixture promotion failure'); END;
            """);
        try { fixture.Store.ShareRedemptionCode(code.Id); throw new Exception("Fixture did not abort promotion"); }
        catch (SqliteException error) when (error.SqliteErrorCode == 19) { checks++; }
        finally { RedemptionSql(fixture.Path, "DROP TRIGGER fixture_abort_promote;"); }
        var store = new Store(fixture.Path);
        var unchanged = store.ListRedemptionCodes().Single();
        Check(unchanged.Mode == "singleUse" && unchanged.Status == "redeemed" && unchanged.ClaimCount == 1
            && store.GetAccount(owner).Balance == 6 && store.GetLedger(owner).Count == 1,
            "A failed promotion leaves the original code and its claimant intact");
        RedemptionThrows(400, "redemption_unavailable", () => store.RedeemCode(second, code.Code));
        Equal(1L, store.ShareRedemptionCode(code.Id).ClaimCount, "Promotion retries successfully after a rolled-back write");

        RedemptionSql(fixture.Path, """
            CREATE TRIGGER fixture_abort_shared_credit BEFORE INSERT ON ledger
            BEGIN SELECT RAISE(ABORT,'fixture ledger failure'); END;
            """);
        try { store.RedeemCode(second, code.Code); throw new Exception("Fixture did not abort the ledger write"); }
        catch (SqliteException error) when (error.SqliteErrorCode == 19) { checks++; }
        finally { RedemptionSql(fixture.Path, "DROP TRIGGER fixture_abort_shared_credit;"); }
        Check(store.GetAccount(second).Balance == 0 && store.GetLedger(second).Count == 0 && store.ListRedemptionCodes().Single().ClaimCount == 1,
            "A ledger failure rolls back the whole shared claim");
        RedemptionSql(fixture.Path, "UPDATE users SET balance=$balance WHERE id=$id", ("$balance", long.MaxValue - 5), ("$id", second));
        RedemptionThrows(409, "credit_limit", () => store.RedeemCode(second, code.Code));
        Equal(1L, store.ListRedemptionCodes().Single().ClaimCount, "A balance overflow leaves shared claim eligibility intact");
        RedemptionSql(fixture.Path, "UPDATE users SET balance=0 WHERE id=$id", ("$id", second));
        var retry = new Store(fixture.Path).RedeemCode(second, code.Code);
        Check(!retry.AlreadyRedeemed && retry.Balance == 6 && store.GetLedger(second).Single().Reference == retry.Reference,
            "After recovery the shared claim can succeed exactly once");
    }
}
