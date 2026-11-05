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

    private static bool RandomShape(string value, string prefix) => value.Length == 34
        && value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
        && !value.AsSpan(2).ContainsAnyExcept("0123456789abcdefABCDEF");

    private static string? CustomRedemptionText(string? code)
    {
        if (code is null || code.Length > 80) return null;
        string text = code.Trim(' ');
        if (text.StartsWith('-') || text.EndsWith('-') || text.Contains("--", StringComparison.Ordinal)
            || text.StartsWith("MU-", StringComparison.OrdinalIgnoreCase) || text.StartsWith("MC-", StringComparison.OrdinalIgnoreCase))
            return null;
        var value = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (c == '-') continue;
            if (!char.IsAsciiLetterOrDigit(c) && c is not (>= '\u3400' and <= '\u4dbf' or >= '\u4e00' and <= '\u9fff')) return null;
            value.Append(char.ToUpperInvariant(c));
        }
        string compact = value.ToString();
        return compact.Length is >= 4 and <= 40 && !RandomShape(compact, "MU") && !RandomShape(compact, "MC") ? compact : null;
    }

    // UTF-8 matches the original ASCII digests for every legacy MU code; equivalent
    // ASCII coupon names also have the same digest, allowing atomic cross-type exclusion.
    private static string RedemptionDigest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string CodeHash(string? code)
    {
        if (string.IsNullOrEmpty(code) || code.Length > 80) throw UnavailableCode();
        string legacy = string.Concat(code.Where(c => c != '-' && !char.IsWhiteSpace(c)));
        if (RandomShape(legacy, "MU")) return RedemptionDigest(legacy.ToUpperInvariant());
        return RedemptionDigest(CustomRedemptionText(code) ?? throw UnavailableCode());
    }

    // The returned plaintext codes exist only here and in the administrator's protected export.
    // The database, metadata list and ledger never contain a redeemable code.
    public IReadOnlyList<GeneratedRedemptionCode> CreateRedemptionCodes(int credits, int count,
        DateTimeOffset? expiresAt, string note)
    {
        using var db = Open(); using var tx = db.BeginTransaction();
        var result = CreateRedemptionCodes(db, tx, credits, count, expiresAt, note);
        tx.Commit();
        return result;
    }

    private static IReadOnlyList<GeneratedRedemptionCode> CreateRedemptionCodes(SqliteConnection db,
        SqliteTransaction tx, int credits, int count, DateTimeOffset? expiresAt, string note, string? customCode = null)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (credits is < 1 or > 1_000_000 || count is < 1 or > 1000 || note is null
            || note.Length > 120 || note.Any(char.IsControl)
            || expiresAt is { } expiry && (expiry <= now || expiry > now.AddYears(10)))
            throw new ApiException(400, "invalid_redemption_options", "积分须为 1–1000000，数量须为 1–1000；有效期须在未来十年内，备注最多 120 字且不得包含控制字符。");
        expiresAt = expiresAt?.ToUniversalTime();
        string? customHash = null;
        if (customCode is not null)
        {
            if (count != 1) throw new ApiException(400, "invalid_redemption_options", "自选口令每次只能生成一个兑换码。");
            var value = CustomRedemptionText(customCode) ?? throw new ApiException(400, "invalid_redemption_code",
                "自选口令需 4–40 个汉字、英文字母或数字，可用单个连字符分组。MU- 和 MC- 前缀及随机码格式为系统保留。");
            customCode = customCode.Trim(' ');
            customHash = RedemptionDigest(value);
            using var used = Command(db, tx, """
                SELECT 1 FROM redemption_codes WHERE code_hash=$hash
                UNION ALL SELECT 1 FROM coupons WHERE code_hash=$hash LIMIT 1
                """, ("$hash", customHash));
            if (used.ExecuteScalar() != null) throw new ApiException(409, "redemption_code_exists", "这个口令已发放过，请使用其他口令。");
        }
        var result = new List<GeneratedRedemptionCode>(count);
        for (int index = 0; index < count; index++)
        {
            string code = customCode ?? RandomRedemptionCode();
            string id = Guid.NewGuid().ToString("N");
            Execute(db, tx, """
                INSERT INTO redemption_codes(id,code_hash,credits,expires_at,created_at,note)
                VALUES($id,$hash,$credits,$expiry,$created,$note)
                """, ("$id", id), ("$hash", customHash ?? CodeHash(code)), ("$credits", credits),
                ("$expiry", expiresAt?.ToString("O", CultureInfo.InvariantCulture)),
                ("$created", now.ToString("O", CultureInfo.InvariantCulture)), ("$note", note.Trim()));
            result.Add(new(id, code, credits, expiresAt));
        }
        return result;
    }

    private static string RandomRedemptionCode()
    {
        string random = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        return "MU-" + string.Join('-', Enumerable.Range(0, 8).Select(i => random.Substring(i * 4, 4)));
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
