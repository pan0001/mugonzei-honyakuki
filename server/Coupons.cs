using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Mugonzei.Server;

public sealed record IssuedCoupon(string Id, string Code, string PlanId, int AmountFen, string AfdianPlanId, DateTimeOffset? ExpiresAt);
public sealed record CouponRecord(string Id, string PlanId, int AmountFen, string AfdianPlanId, DateTimeOffset? ExpiresAt, DateTimeOffset? RevokedAt);
public sealed record PriceQuote(string PlanId, int Credits, int OriginalAmountFen, int AmountFen)
{
    public int DiscountFen => OriginalAmountFen - AmountFen;
}

public sealed partial class Store
{
    private static void InitializeCouponSchema(SqliteConnection db) => Execute(db, null, """
        CREATE TABLE IF NOT EXISTS coupons (
          id TEXT PRIMARY KEY, code_hash TEXT NOT NULL UNIQUE, plan_id TEXT NOT NULL,
          base_amount_fen INTEGER NOT NULL, base_credits INTEGER NOT NULL, base_afdian_plan_id TEXT NOT NULL,
          amount_fen INTEGER NOT NULL CHECK(amount_fen > 0 AND amount_fen < base_amount_fen),
          afdian_plan_id TEXT NOT NULL, created_at TEXT NOT NULL, expires_at TEXT, revoked_at TEXT);
        CREATE INDEX IF NOT EXISTS coupons_afdian_plan ON coupons(afdian_plan_id);
        CREATE INDEX IF NOT EXISTS coupons_base_afdian_plan ON coupons(base_afdian_plan_id COLLATE NOCASE);
        CREATE INDEX IF NOT EXISTS orders_afdian_plan ON orders(afdian_plan_id COLLATE NOCASE);
        CREATE TABLE IF NOT EXISTS order_coupons (
          order_id TEXT PRIMARY KEY REFERENCES orders(id), coupon_id TEXT NOT NULL REFERENCES coupons(id),
          original_amount_fen INTEGER NOT NULL CHECK(original_amount_fen > 0));
        """);

    private static ApiException CouponUnavailable() => new(400, "coupon_unavailable", "优惠码无效、已失效或不适用于此套餐。");
    private static string CouponHash(string code)
    {
        if (code.Length is 0 or > 80) throw CouponUnavailable();
        var normalized = new StringBuilder(code.Length);
        foreach (char c in code)
        {
            if (c is '-' or ' ') continue;
            if (!char.IsAsciiLetterOrDigit(c)) throw CouponUnavailable();
            normalized.Append(char.ToUpperInvariant(c));
        }
        string value = normalized.ToString();
        if (value.Length is < 4 or > 40 || value.StartsWith("MU", StringComparison.Ordinal)) throw CouponUnavailable();
        return Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(value)));
    }

    private static Plan CheckoutPlan(IReadOnlyList<Plan> plans, string planId) =>
        plans.FirstOrDefault(p => p.Id == planId) ?? throw new ApiException(400, "unknown_plan", "充值计划不存在。");

    private static bool IsBaseProviderPlan(IReadOnlyList<Plan> plans, string providerId) =>
        plans.Any(p => string.Equals(p.AfdianPlanId, providerId, StringComparison.OrdinalIgnoreCase));

    private static void RequireBaseProvider(SqliteConnection db, SqliteTransaction? tx, string baseId)
    {
        using var command = Command(db, tx, "SELECT 1 FROM coupons WHERE afdian_plan_id=$id LIMIT 1",
            ("$id", baseId.ToLowerInvariant()));
        if (command.ExecuteScalar() is not null)
            throw new ApiException(409, "coupon_plan_conflict", "该套餐的爱发电原价方案与历史优惠方案冲突。");
    }

    public IssuedCoupon CreateCoupon(IReadOnlyList<Plan> plans, string planId, int amountFen, string afdianPlanId,
        DateTimeOffset? expiresAt = null, string? customCode = null, Guid? issuanceRequestId = null)
    {
        var plan = CheckoutPlan(plans, planId);
        if (amountFen < 1 || amountFen >= plan.AmountFen || afdianPlanId is not { Length: 32 } ||
            !afdianPlanId.All(char.IsAsciiHexDigit) || IsBaseProviderPlan(plans, afdianPlanId) ||
            expiresAt <= DateTimeOffset.UtcNow)
            throw new ApiException(400, "invalid_coupon", "请填写正数折后价、独立的爱发电折扣方案 ID 和有效期限。");
        string code = customCode?.ToUpperInvariant() ?? "MC-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        if (customCode is not null && (customCode.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-') ||
            customCode.StartsWith('-') || customCode.EndsWith('-') || customCode.Contains("--", StringComparison.Ordinal)))
            throw CouponUnavailable();
        string hash = CouponHash(code);
        string id = Guid.NewGuid().ToString("N");
        string providerId = afdianPlanId.ToLowerInvariant();
        expiresAt = expiresAt?.ToUniversalTime();
        using var db = Open();
        using var tx = db.BeginTransaction();
        if (issuanceRequestId is { } key && !ReserveAdminIssuance(db, tx, "coupon", key))
            throw new ApiException(409, "coupon_issue_exists", "该创建请求已处理，请查看活动列表。优惠码仅在首次创建时显示。");
        RequireBaseProvider(db, tx, plan.AfdianPlanId);
        // Keep retired full-price provider IDs separate even after the operator removes their base plan.
        using (var wasBase = Command(db, tx, """
            SELECT 1 FROM coupons WHERE base_afdian_plan_id=$provider COLLATE NOCASE
            UNION ALL SELECT 1 FROM orders WHERE afdian_plan_id=$provider COLLATE NOCASE
              AND NOT EXISTS (SELECT 1 FROM order_coupons WHERE order_id=orders.id)
            LIMIT 1
            """, ("$provider", providerId)))
            if (wasBase.ExecuteScalar() is not null)
                throw new ApiException(409, "coupon_plan_conflict", "该爱发电方案曾用于原价套餐，不能作为折扣方案。");
        // A provider plan has one price and benefit. Never reinterpret its identifier for another offer.
        using (var conflict = Command(db, tx, """
            SELECT 1 FROM coupons WHERE afdian_plan_id=$provider AND
              (plan_id<>$plan OR amount_fen<>$price OR base_credits<>$credits) LIMIT 1
            """, ("$provider", providerId), ("$plan", plan.Id), ("$price", amountFen), ("$credits", plan.Credits)))
            if (conflict.ExecuteScalar() is not null)
                throw new ApiException(409, "coupon_plan_conflict", "该爱发电折扣方案已绑定其他价格或套餐。");
        try
        {
            Execute(db, tx, """
                INSERT INTO coupons(id,code_hash,plan_id,base_amount_fen,base_credits,base_afdian_plan_id,
                  amount_fen,afdian_plan_id,created_at,expires_at)
                VALUES($id,$hash,$plan,$base,$credits,$baseProvider,$price,$provider,$now,$expiry)
                """, ("$id", id), ("$hash", hash), ("$plan", plan.Id), ("$base", plan.AmountFen), ("$credits", plan.Credits),
                ("$baseProvider", plan.AfdianPlanId), ("$price", amountFen), ("$provider", providerId), ("$now", Now()),
                ("$expiry", expiresAt?.ToString("O", CultureInfo.InvariantCulture)));
        }
        catch (SqliteException ex) when (ex.SqliteExtendedErrorCode == 2067)
        { throw new ApiException(409, "coupon_exists", "此优惠口令已发放过，请使用新的口令。"); }
        tx.Commit();
        return new(id, code, plan.Id, amountFen, providerId, expiresAt);
    }

    public IReadOnlyList<CouponRecord> ListCoupons(int limit = 100)
    {
        if (limit is < 1 or > 1000) throw new ApiException(400, "invalid_limit", "查询数量须为 1 至 1000。");
        using var db = Open();
        using var command = Command(db, null, """
            SELECT id,plan_id,amount_fen,afdian_plan_id,expires_at,revoked_at
            FROM coupons ORDER BY created_at DESC,id LIMIT $limit
            """, ("$limit", limit));
        using var reader = command.ExecuteReader();
        var result = new List<CouponRecord>();
        while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3),
            reader.IsDBNull(4) ? null : Date(reader.GetString(4)), reader.IsDBNull(5) ? null : Date(reader.GetString(5))));
        return result;
    }

    public bool RevokeCoupon(string id)
    {
        using var db = Open();
        return Execute(db, null, "UPDATE coupons SET revoked_at=$now WHERE id=$id AND revoked_at IS NULL",
            ("$now", Now()), ("$id", Id(id))) == 1;
    }

    private sealed record CheckoutPrice(PriceQuote Quote, string ProviderPlanId, string? CouponId);
    private static CheckoutPrice ResolvePrice(SqliteConnection db, SqliteTransaction? tx, IReadOnlyList<Plan> plans,
        Plan plan, string? couponHash)
    {
        RequireBaseProvider(db, tx, plan.AfdianPlanId);
        if (couponHash is null) return new(new(plan.Id, plan.Credits, plan.AmountFen, plan.AmountFen), plan.AfdianPlanId, null);
        using var command = Command(db, tx, """
            SELECT id,amount_fen,afdian_plan_id FROM coupons WHERE code_hash=$hash AND plan_id=$plan AND
              base_amount_fen=$base AND base_credits=$credits AND base_afdian_plan_id=$baseProvider COLLATE NOCASE AND
              revoked_at IS NULL AND (expires_at IS NULL OR expires_at>$now)
            """, ("$hash", couponHash), ("$plan", plan.Id), ("$base", plan.AmountFen), ("$credits", plan.Credits),
            ("$baseProvider", plan.AfdianPlanId), ("$now", Now()));
        using var row = command.ExecuteReader();
        if (!row.Read() || IsBaseProviderPlan(plans, row.GetString(2))) throw CouponUnavailable();
        return new(new(plan.Id, plan.Credits, plan.AmountFen, row.GetInt32(1)), row.GetString(2), row.GetString(0));
    }

    internal PriceQuote Quote(IReadOnlyList<Plan> plans, string planId, string? couponCode)
    {
        using var db = Open();
        return ResolvePrice(db, null, plans, CheckoutPlan(plans, planId), couponCode is null ? null : CouponHash(couponCode)).Quote;
    }

    private static void ConfirmPrice(int actual, int? expected, bool couponRequested)
    {
        if ((couponRequested || expected.HasValue) && actual != expected)
            throw new ApiException(409, "price_changed", "订单金额与确认金额不一致，请重新获取报价。");
    }
}
