using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace Mugonzei.Server;

public static class AdminRedemptions
{
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record Batch(int Credits, int Count, int Days, string? Note);

    public static void Map(RouteGroupBuilder authorized)
    {
        // The parent admin group applies the session, same-origin write checks and no-store policy.
        var codes = authorized.MapGroup("/redemptions");
        codes.MapGet("", (Store store, int? limit) => store.ListRedemptionCodes(limit ?? 100).Select(Metadata));
        codes.MapPost("", (HttpRequest request, Batch input, Store store) =>
        {
            var key = AdminIssuance.RequestKey(request);
            if (input.Count is < 1 or > 50 || input.Days is < 1 or > 365)
                throw new ApiException(400, "invalid_redemption_options", "每批可生成 1–50 个兑换码，有效期须为 1–365 天。");
            var issued = store.IssueAdminRedemptions(key, input.Credits, input.Count, input.Days, input.Note ?? "");
            return Results.Json(new { codes = issued }, statusCode: StatusCodes.Status201Created);
        });
        codes.MapPost("/{id}/revoke", (string id, JsonElement body, Store store) =>
        {
            if (body.ValueKind != JsonValueKind.Object || body.EnumerateObject().Any())
                throw new ApiException(400, "invalid_revoke_request", "撤销请求需要一个空 JSON 对象。");
            return Metadata(store.RevokeRedemptionCode(id));
        });
    }

    // Explicit allowlist. The internal record also carries the redeemer's account ID.
    private static object Metadata(RedemptionCodeRecord row) => new
    {
        row.Id, row.Credits, row.ExpiresAt, row.CreatedAt, row.Note, row.Status, row.RedeemedAt
    };
}

internal static class AdminIssuance
{
    internal static Guid RequestKey(HttpRequest request)
    {
        if (!request.Headers.TryGetValue("Idempotency-Key", out var keys) || keys.Count != 1
            || keys[0] is not { Length: 36 } key || !Guid.TryParseExact(key, "D", out var id) || id == Guid.Empty)
            throw new ApiException(400, "invalid_idempotency_key", "请为本次生成操作提供有效的请求编号。");
        return id;
    }
}

public sealed partial class Store
{
    // Commit the marker and all issued items together. Never persist a plaintext response to replay.
    internal static bool ReserveAdminIssuance(SqliteConnection db, SqliteTransaction tx, string purpose, Guid requestId)
    {
        Execute(db, tx, """
            CREATE TABLE IF NOT EXISTS admin_issuance_requests (
              purpose TEXT NOT NULL, request_id TEXT NOT NULL, created_at TEXT NOT NULL,
              PRIMARY KEY(purpose, request_id)) WITHOUT ROWID
            """);
        return Execute(db, tx, """
            INSERT INTO admin_issuance_requests(purpose,request_id,created_at) VALUES($purpose,$request,$now)
            ON CONFLICT(purpose,request_id) DO NOTHING
            """, ("$purpose", purpose), ("$request", requestId.ToString("D")), ("$now", Now())) == 1;
    }

    internal IReadOnlyList<GeneratedRedemptionCode> IssueAdminRedemptions(Guid requestId, int credits, int count, int days, string note)
    {
        using var db = Open(); using var tx = db.BeginTransaction();
        if (!ReserveAdminIssuance(db, tx, "redemption", requestId))
            throw new ApiException(409, "redemption_batch_exists", "该请求已生成兑换码。请查看列表核对，明文仅在首次成功时显示。");
        var issued = CreateRedemptionCodes(db, tx, credits, count, DateTimeOffset.UtcNow.AddDays(days), note);
        tx.Commit();
        return issued;
    }
}
