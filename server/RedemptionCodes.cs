using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Mugonzei.Server;

public sealed record GeneratedRedemptionCode(string Id, string Code, int Credits, DateTimeOffset? ExpiresAt);
public sealed record RedemptionResult(int Credits, long Balance, long Reserved, bool AlreadyRedeemed, string Reference);
public sealed record RedemptionCodeRecord(string Id, int Credits, DateTimeOffset? ExpiresAt,
    DateTimeOffset CreatedAt, string Note, string Status, DateTimeOffset? RedeemedAt, string? RedeemedBy);
public sealed record RedeemCodeRequest(string? Code);

public sealed partial class Store
{
    private static void InitializeRedemptionSchema(SqliteConnection db) => Execute(db, null, """
        CREATE TABLE IF NOT EXISTS redemption_codes (
          id TEXT PRIMARY KEY, code_hash TEXT NOT NULL UNIQUE,
          credits INTEGER NOT NULL CHECK(credits BETWEEN 1 AND 1000000),
          expires_at TEXT, created_at TEXT NOT NULL, note TEXT NOT NULL,
          revoked_at TEXT, redeemed_by TEXT REFERENCES users(id), redeemed_at TEXT,
          CHECK((redeemed_by IS NULL AND redeemed_at IS NULL) OR
                (redeemed_by IS NOT NULL AND redeemed_at IS NOT NULL)),
          CHECK(revoked_at IS NULL OR redeemed_by IS NULL));
        CREATE INDEX IF NOT EXISTS redemption_codes_created ON redemption_codes(created_at,id);
        CREATE INDEX IF NOT EXISTS redemption_codes_user ON redemption_codes(redeemed_by);
        """);

    private static ApiException UnavailableCode() => new(400, "redemption_unavailable", "兑换码无效、已被使用或已失效，请核对后重试。");
    private static string CodeHash(string? code)
    {
        if (string.IsNullOrEmpty(code) || code.Length > 80) throw UnavailableCode();
        var normalized = new StringBuilder(34);
        foreach (char c in code)
        {
            if (c == '-' || char.IsWhiteSpace(c)) continue;
            if (c > 127) throw UnavailableCode();
            normalized.Append(char.ToUpperInvariant(c));
        }
        string value = normalized.ToString();
        if (value.Length != 34 || !value.StartsWith("MU", StringComparison.Ordinal)
            || value.AsSpan(2).ContainsAnyExcept("0123456789ABCDEF")) throw UnavailableCode();
        return Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(value)));
    }

    // The returned plaintext codes exist only here and in the administrator's protected export.
    // The database, metadata list and ledger never contain a redeemable code.
    public IReadOnlyList<GeneratedRedemptionCode> CreateRedemptionCodes(int credits, int count,
        DateTimeOffset? expiresAt, string note)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (credits is < 1 or > 1_000_000 || count is < 1 or > 1000 || note is null
            || note.Length > 120 || note.Any(char.IsControl)
            || expiresAt is { } expiry && (expiry <= now || expiry > now.AddYears(10)))
            throw new ApiException(400, "invalid_redemption_options", "积分须为 1–1000000，数量须为 1–1000；有效期须在未来十年内，备注最多 120 字且不得包含控制字符。");
        expiresAt = expiresAt?.ToUniversalTime();
        using var db = Open(); using var tx = db.BeginTransaction();
        var result = new List<GeneratedRedemptionCode>(count);
        for (int index = 0; index < count; index++)
        {
            string random = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            string code = "MU-" + string.Join('-', Enumerable.Range(0, 8).Select(i => random.Substring(i * 4, 4)));
            string id = Guid.NewGuid().ToString("N");
            Execute(db, tx, """
                INSERT INTO redemption_codes(id,code_hash,credits,expires_at,created_at,note)
                VALUES($id,$hash,$credits,$expiry,$created,$note)
                """, ("$id", id), ("$hash", CodeHash(code)), ("$credits", credits),
                ("$expiry", expiresAt?.ToString("O", CultureInfo.InvariantCulture)),
                ("$created", now.ToString("O", CultureInfo.InvariantCulture)), ("$note", note.Trim()));
            result.Add(new(id, code, credits, expiresAt));
        }
        tx.Commit(); return result;
    }

    private sealed record CodeRow(string Id, int Credits, DateTimeOffset? ExpiresAt, DateTimeOffset CreatedAt,
        string Note, DateTimeOffset? RevokedAt, string? RedeemedBy, DateTimeOffset? RedeemedAt)
    {
        public RedemptionCodeRecord Metadata() => new(Id, Credits, ExpiresAt, CreatedAt, Note,
            RedeemedBy is not null ? "redeemed" : RevokedAt is not null ? "revoked"
            : ExpiresAt <= DateTimeOffset.UtcNow ? "expired" : "active", RedeemedAt, RedeemedBy);
    }
    private const string CodeColumns = "id,credits,expires_at,created_at,note,revoked_at,redeemed_by,redeemed_at";
    private static CodeRow ReadCodeRow(SqliteDataReader row) => new(row.GetString(0), row.GetInt32(1),
        row.IsDBNull(2) ? null : Date(row.GetString(2)), Date(row.GetString(3)), row.GetString(4),
        row.IsDBNull(5) ? null : Date(row.GetString(5)), row.IsDBNull(6) ? null : row.GetString(6),
        row.IsDBNull(7) ? null : Date(row.GetString(7)));
    private static CodeRow? ReadCode(SqliteConnection db, SqliteTransaction? tx, string field, string value)
    {
        // The field is selected only by the two internal call sites, never from user input.
        using var command = Command(db, tx, "SELECT " + CodeColumns + " FROM redemption_codes WHERE " + field + "=$value", ("$value", value));
        using var reader = command.ExecuteReader(); return reader.Read() ? ReadCodeRow(reader) : null;
    }

    public RedemptionResult RedeemCode(string userId, string? code)
    {
        string hash = CodeHash(code);
        using var db = Open(); using var tx = db.BeginTransaction();
        var account = Account(db, tx, userId);
        var row = ReadCode(db, tx, "code_hash", hash) ?? throw UnavailableCode();
        string reference = "redemption:" + row.Id;
        if (row.RedeemedBy == userId)
        {
            tx.Commit();
            return new(row.Credits, account.Balance, account.Reserved, true, reference);
        }
        if (row.RedeemedBy is not null || row.RevokedAt is not null || row.ExpiresAt <= DateTimeOffset.UtcNow)
            throw UnavailableCode();
        if (account.Balance > long.MaxValue - row.Credits)
            throw new ApiException(409, "credit_limit", "账户积分已达到上限，本次兑换未入账，兑换码仍可使用。");
        if (Execute(db, tx, """
            UPDATE redemption_codes SET redeemed_by=$user,redeemed_at=$now
            WHERE id=$id AND redeemed_by IS NULL AND revoked_at IS NULL
            """, ("$user", userId), ("$now", Now()), ("$id", row.Id)) != 1) throw UnavailableCode();
        Execute(db, tx, "UPDATE users SET balance=balance+$credits WHERE id=$user", ("$credits", row.Credits), ("$user", userId));
        Ledger(db, tx, userId, "redemption", row.Credits, reference);
        account = Account(db, tx, userId);
        tx.Commit(); return new(row.Credits, account.Balance, account.Reserved, false, reference);
    }

    public IReadOnlyList<RedemptionCodeRecord> ListRedemptionCodes(int limit = 100)
    {
        if (limit is < 1 or > 1000) throw new ApiException(400, "invalid_redemption_options", "查询数量须为 1–1000。");
        using var db = Open(); using var command = Command(db, null,
            "SELECT " + CodeColumns + " FROM redemption_codes ORDER BY created_at DESC,id DESC LIMIT $limit", ("$limit", limit));
        using var reader = command.ExecuteReader(); var rows = new List<RedemptionCodeRecord>();
        while (reader.Read()) rows.Add(ReadCodeRow(reader).Metadata());
        return rows;
    }

    public RedemptionCodeRecord RevokeRedemptionCode(string id)
    {
        id = Id(id); using var db = Open(); using var tx = db.BeginTransaction();
        var row = ReadCode(db, tx, "id", id) ?? throw Missing();
        if (row.RedeemedBy is not null)
            throw new ApiException(409, "redemption_already_used", "该兑换码已入账，不能撤销或扣回用户积分。");
        if (row.RevokedAt is null)
            Execute(db, tx, "UPDATE redemption_codes SET revoked_at=$now WHERE id=$id", ("$now", Now()), ("$id", id));
        row = ReadCode(db, tx, "id", id)!;
        tx.Commit(); return row.Metadata();
    }
}
