using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace Mugonzei.Server;

public sealed partial class Store
{
    private static void InitializeEmailSchema(SqliteConnection db) => Execute(db, null, """
        CREATE TABLE IF NOT EXISTS account_emails (
          user_id TEXT PRIMARY KEY REFERENCES users(id), email TEXT NOT NULL UNIQUE, verified_at INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS registration_email_challenges (
          email_key TEXT PRIMARY KEY, delivery_id TEXT NOT NULL, code_hash BLOB NOT NULL, expires_at INTEGER NOT NULL,
          delivered INTEGER NOT NULL DEFAULT 0, attempts INTEGER NOT NULL DEFAULT 0);
        CREATE TABLE IF NOT EXISTS registration_email_requests (
          email_key TEXT NOT NULL, ip_key TEXT NOT NULL, sent_at INTEGER NOT NULL);
        CREATE INDEX IF NOT EXISTS email_requests_by_time ON registration_email_requests(sent_at);
        CREATE INDEX IF NOT EXISTS email_requests_by_address ON registration_email_requests(email_key,sent_at);
        CREATE INDEX IF NOT EXISTS email_requests_by_ip ON registration_email_requests(ip_key,sent_at);
        """);

    private static void PruneEmailData(SqliteConnection db, SqliteTransaction tx, long now)
    {
        Execute(db, tx, "DELETE FROM registration_email_challenges WHERE expires_at <= $now", ("$now", now));
        Execute(db, tx, "DELETE FROM registration_email_requests WHERE sent_at <= $cutoff", ("$cutoff", now - 86400));
    }

    internal bool ReserveEmailDelivery(string email, string emailKey, string ipKey, string delivery, byte[] hash,
        DateTimeOffset issued, int dailyLimit)
    {
        long now = issued.ToUnixTimeSeconds();
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: false);
        PruneEmailData(db, tx, now);
        long Count(string sql, params (string, object?)[] args)
        {
            using var query = Command(db, tx, sql, args);
            return (long)query.ExecuteScalar()!;
        }
        if (Count("SELECT COUNT(*) FROM registration_email_requests WHERE email_key=$email AND sent_at>$cutoff", ("$email", emailKey), ("$cutoff", now-60)) != 0
            || Count("SELECT COUNT(*) FROM registration_email_requests WHERE email_key=$email", ("$email", emailKey)) >= 6
            || Count("SELECT COUNT(*) FROM registration_email_requests WHERE ip_key=$ip AND sent_at>$cutoff", ("$ip", ipKey), ("$cutoff", now-3600)) >= 20
            || Count("SELECT COUNT(*) FROM registration_email_requests") >= dailyLimit)
            throw new ApiException(429, "email_rate_limited", "验证码请求过于频繁，请稍后重试。");
        Execute(db, tx, "INSERT INTO registration_email_requests(email_key,ip_key,sent_at) VALUES($email,$ip,$now)",
            ("$email", emailKey), ("$ip", ipKey), ("$now", now));
        bool needsEmail = Count("SELECT COUNT(*) FROM account_emails WHERE email=$email", ("$email", email)) == 0;
        if (needsEmail)
            Execute(db, tx, """
                INSERT INTO registration_email_challenges(email_key,delivery_id,code_hash,expires_at) VALUES($email,$delivery,$hash,$expiry)
                ON CONFLICT(email_key) DO UPDATE SET delivery_id=excluded.delivery_id,code_hash=excluded.code_hash,
                expires_at=excluded.expires_at,delivered=0,attempts=0
                """, ("$email", emailKey), ("$delivery", delivery), ("$hash", hash), ("$expiry", now+600));
        tx.Commit();
        return needsEmail;
    }

    internal void FinishEmailDelivery(string emailKey, string delivery, bool delivered)
    {
        using var db = Open();
        string sql = delivered
            ? "UPDATE registration_email_challenges SET delivered=1 WHERE email_key=$email AND delivery_id=$id"
            : "DELETE FROM registration_email_challenges WHERE email_key=$email AND delivery_id=$id";
        Execute(db, null, sql, ("$email", emailKey), ("$id", delivery));
    }

    internal void VerifyRegistrationCode(PresentedEmailCode code, DateTimeOffset now)
    {
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: false);
        VerifyRegistrationCode(db, tx, code, now.ToUnixTimeSeconds());
        tx.Commit();
    }

    private static void VerifyRegistrationCode(SqliteConnection db, SqliteTransaction tx, PresentedEmailCode code, long instant)
    {
        using var lookup = Command(db, tx, """
            SELECT code_hash FROM registration_email_challenges
            WHERE email_key=$email AND delivered=1 AND expires_at>$now AND attempts<5
            """, ("$email", code.EmailKey), ("$now", instant));
        if (lookup.ExecuteScalar() is not byte[] expected)
            throw RegistrationEmailService.InvalidCode();
        if (!CryptographicOperations.FixedTimeEquals(code.Digest, expected))
        {
            Execute(db, tx, "UPDATE registration_email_challenges SET attempts=attempts+1 WHERE email_key=$email", ("$email", code.EmailKey));
            tx.Commit();
            throw RegistrationEmailService.InvalidCode();
        }
    }

    internal string RegisterVerifiedUser(PresentedEmailCode code, DateTimeOffset now, string username, byte[] passwordHash, byte[] salt, int iterations)
    {
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: false);
        long instant = now.ToUnixTimeSeconds();
        VerifyRegistrationCode(db, tx, code, instant);
        string id = InsertUser(db, tx, username, passwordHash, salt, iterations);
        Execute(db, tx, "INSERT INTO account_emails(user_id,email,verified_at) VALUES($id,$email,$now)",
            ("$id", id), ("$email", code.Email), ("$now", instant));
        Execute(db, tx, "DELETE FROM registration_email_challenges WHERE email_key=$email", ("$email", code.EmailKey));
        tx.Commit();
        return id;
    }
}
