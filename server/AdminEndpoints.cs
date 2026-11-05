using System.Text.Json.Serialization;

namespace Mugonzei.Server;

public static class AdminEndpoints
{
    private sealed record ApiRules(bool SessionRequired);
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record LoginRequest(string? Token);
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record CreateCouponRequest(string PlanId, int AmountFen, string AfdianPlanId, int? Days, string? CustomCode);

    public static void Map(WebApplication app)
    {
        var clock = app.Services.GetRequiredService<TimeProvider>();
        var access = AdminAccess.Load(app.Configuration, app.Environment, clock);
        if (access is null) return;
        AdminPages.Map(app, access.AllowsOriginHost);

        // Check the selected route before minimal API binding reads its JSON body. The protected
        // metadata follows group routes, including additional operations registered by other modules.
        app.Use(async (context, next) =>
        {
            if (context.GetEndpoint()?.Metadata.GetMetadata<ApiRules>() is { } rules)
            {
                context.Response.Headers.CacheControl = "no-store";
                if (!access.AllowsOriginHost(context))
                    throw new ApiException(404, "not_found", "页面不存在。");
                if (!HttpMethods.IsGet(context.Request.Method) && !AdminAccess.IsSameOriginWrite(context.Request))
                    throw new ApiException(403, "admin_origin_denied", "请从同源的开发者后台提交。");
                if (rules.SessionRequired && !access.SessionExpiry(context.Request).HasValue)
                    throw new ApiException(401, "admin_unauthorized", "请登录开发者后台。");
            }
            await next(context);
        });

        var session = app.MapGroup("/admin/api/session").WithMetadata(new ApiRules(false));
        session.MapGet("", (HttpContext context) =>
        {
            var expiresAt = access.SessionExpiry(context.Request);
            return new { authenticated = expiresAt.HasValue, expiresAt };
        });
        session.MapPost("", (LoginRequest body, HttpContext context) =>
            new { authenticated = true, expiresAt = access.Login(context, body.Token) }).RequireRateLimiting("admin-login");
        session.MapDelete("", (HttpContext context) => { access.Logout(context); return Results.NoContent(); });

        var admin = app.MapGroup("/admin/api").WithMetadata(new ApiRules(true));
        admin.MapGet("/plans", (ServerOptions options) => options.Plans.Select(p => new { p.Id, p.Name, p.Credits, p.AmountFen, p.AfdianPlanId }));
        admin.MapGet("/coupons", (Store store) => store.ListCoupons());
        admin.MapPost("/coupons", (CreateCouponRequest body, HttpContext context, Store store, ServerOptions options) =>
        {
            Guid key = AdminIssuance.RequestKey(context.Request);
            int days = body.Days ?? 30;
            if (days is < 1 or > 365) throw new ApiException(400, "invalid_coupon", "活动有效期须为 1 至 365 天。");
            var issued = store.CreateCoupon(options.Plans, body.PlanId, body.AmountFen, body.AfdianPlanId,
                DateTimeOffset.UtcNow.AddDays(days), body.CustomCode, key);
            return Results.Json(issued, statusCode: StatusCodes.Status201Created);
        });
        admin.MapPost("/coupons/{id}/revoke", (string id, Store store) => new { id, revoked = store.RevokeCoupon(id) });
        AdminRedemptions.Map(admin);
        AdminSummary.Map(admin);
    }
}
