using Microsoft.Data.Sqlite;

namespace Mugonzei.Server;

internal sealed record PaymentCheck(string TradeNo, long ReceivedAt, int Attempt);

public sealed partial class Store
{
    internal const int PaymentCheckCapacity = 512;
    private const int PaymentCheckLifetimeSeconds = 24 * 60 * 60;

    private static void InitializePaymentVerifications(SqliteConnection db) => Execute(db, null, """
        CREATE TABLE IF NOT EXISTS payment_verifications (
          trade_no TEXT PRIMARY KEY, received_at INTEGER NOT NULL, next_at INTEGER NOT NULL, attempts INTEGER NOT NULL
        ) WITHOUT ROWID;
        CREATE INDEX IF NOT EXISTS payment_verifications_due ON payment_verifications(next_at,received_at);
        CREATE INDEX IF NOT EXISTS payment_verifications_received ON payment_verifications(received_at);
        """);

    internal void EnqueuePaymentVerification(string tradeNo, DateTimeOffset received)
    {
        long now = received.ToUnixTimeSeconds();
        using var db = Open(); using var tx = db.BeginTransaction();
        // A duplicate does not change the original deadline or consume another admission.
        // Check before expiry cleanup so a replay cannot refresh an existing record at its cutoff.
        using var known = Command(db, tx, """
            SELECT 1 FROM orders WHERE provider_trade_no=$trade
            UNION ALL SELECT 1 FROM payment_verifications WHERE trade_no=$trade LIMIT 1
            """, ("$trade", tradeNo));
        if (known.ExecuteScalar() is not null) { tx.Commit(); return; }
        PrunePaymentChecks(db, tx, now);
        int inserted = Execute(db, tx, """
            INSERT INTO payment_verifications(trade_no,received_at,next_at,attempts)
            SELECT $trade,$now,$now,0 WHERE (SELECT COUNT(*) FROM payment_verifications) < $capacity
            """, ("$trade", tradeNo), ("$now", now), ("$capacity", PaymentCheckCapacity));
        if (inserted == 0) throw new ApiException(503, "payment_verification_busy", "支付核验队列暂满，请稍后重试。");
        tx.Commit();
    }

    private static void PrunePaymentChecks(SqliteConnection db, SqliteTransaction tx, long now) =>
        Execute(db, tx, "DELETE FROM payment_verifications WHERE received_at <= $cutoff", ("$cutoff", now - PaymentCheckLifetimeSeconds));

    internal PaymentCheck? ClaimPaymentCheck(DateTimeOffset time)
    {
        long now = time.ToUnixTimeSeconds();
        using var db = Open();
        // Most polls are idle. Indexed reads avoid acquiring a writer lock while waiting.
        using var due = Command(db, null, """
            SELECT EXISTS(SELECT 1 FROM payment_verifications WHERE next_at <= $now)
                OR EXISTS(SELECT 1 FROM payment_verifications WHERE received_at <= $cutoff)
            """, ("$now", now), ("$cutoff", now - PaymentCheckLifetimeSeconds));
        if (Convert.ToInt64(due.ExecuteScalar()) == 0) return null;
        using var tx = db.BeginTransaction();
        PrunePaymentChecks(db, tx, now);
        // A manual refresh can settle a payment while its notification is still queued.
        Execute(db, tx, """
            DELETE FROM payment_verifications WHERE EXISTS
              (SELECT 1 FROM orders WHERE provider_trade_no=payment_verifications.trade_no)
            """);
        using var claim = Command(db, tx, """
            UPDATE payment_verifications SET next_at=$lease, attempts=attempts+1
            WHERE trade_no=(SELECT trade_no FROM payment_verifications WHERE next_at <= $now
                            ORDER BY next_at,received_at,trade_no LIMIT 1)
            RETURNING trade_no,received_at,attempts
            """, ("$lease", now + 60), ("$now", now));
        PaymentCheck? job;
        using (var reader = claim.ExecuteReader())
            job = reader.Read() ? new(reader.GetString(0), reader.GetInt64(1), reader.GetInt32(2)) : null;
        tx.Commit();
        return job;
    }

    internal void FinishPaymentCheck(PaymentCheck job, DateTimeOffset? retryAt)
    {
        // If the host stops after claiming a job it becomes available after its one-minute lease.
        // A late result must not overwrite a newer claim or reschedule a new notification.
        using var db = Open();
        using var cmd = Command(db, null, (retryAt.HasValue ? "UPDATE payment_verifications SET next_at=$retry" : "DELETE FROM payment_verifications")
            + " WHERE trade_no=$trade AND received_at=$received AND attempts=$attempt",
            ("$trade", job.TradeNo), ("$received", job.ReceivedAt), ("$attempt", job.Attempt));
        if (retryAt.HasValue) cmd.Parameters.AddWithValue("$retry", retryAt.Value.ToUnixTimeSeconds());
        cmd.ExecuteNonQuery();
    }
}

internal sealed class PaymentReconciliationWorker(Store store, PaymentService payments, TimeProvider clock,
    ILogger<PaymentReconciliationWorker> logger) : BackgroundService
{
    private static readonly int[] RetrySeconds = [5, 15, 60, 300, 900, 1800, 3600];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            int delay = 10;
            try { if (await ProcessNextAsync(stoppingToken)) delay = 2; }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                // Keep the service running after local/transient failures; the lease is recoverable.
                logger.LogWarning("Payment verification worker will retry after {ErrorType}", ex.GetType().Name);
            }
            await Task.Delay(TimeSpan.FromSeconds(delay), clock, stoppingToken);
        }
    }

    internal async Task<bool> ProcessNextAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var job = store.ClaimPaymentCheck(clock.GetUtcNow());
        if (job is null) return false;
        bool done;
        try { done = await payments.VerifyNotificationAsync(job.TradeNo, token); }
        catch (ApiException ex) when (ex.Code is "payment_mismatch" or "payment_used")
        {
            logger.LogWarning("Signed payment details failed local validation: {Code}", ex.Code);
            done = true;
        }
        catch (ApiException ex)
        {
            logger.LogWarning("Payment verification deferred: {Code}", ex.Code);
            done = false;
        }
        int delay = RetrySeconds[Math.Min(job.Attempt, RetrySeconds.Length) - 1];
        store.FinishPaymentCheck(job, done ? null : clock.GetUtcNow().AddSeconds(delay));
        return true;
    }
}
