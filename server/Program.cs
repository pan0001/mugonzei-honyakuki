using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Mugonzei.Server;

if (args.Length > 0 && args[0] == "refund") { Environment.ExitCode = RefundCommands.Run(args); return; }
if (args.Length > 0 && args[0] == "codes") { Environment.ExitCode = RedemptionCommands.Run(args); return; }
var builder = WebApplication.CreateBuilder(args);
var options = ServerOptions.Load(builder.Configuration, builder.Environment);
using var databaseLease = new DatabaseLease(options.DatabasePath);
string urls = builder.WebHost.GetSetting("urls") ?? "http://127.0.0.1:5187";
builder.WebHost.UseUrls(urls);
var trustedProxies = new List<IPAddress>();
foreach (string value in (builder.Configuration["MUGONZEI_TRUSTED_PROXIES"] ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
{
    if (!IPAddress.TryParse(value, out var address) || trustedProxies.Count >= 16)
        throw new InvalidOperationException("MUGONZEI_TRUSTED_PROXIES 只接受最多 16 个明确的代理 IP 地址。");
    trustedProxies.Add(address);
}
if (trustedProxies.Count > 0)
    builder.Services.Configure<ForwardedHeadersOptions>(forwarded =>
    {
        forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        forwarded.ForwardLimit = 1;
        forwarded.KnownIPNetworks.Clear(); forwarded.KnownProxies.Clear();
        foreach (var address in trustedProxies) forwarded.KnownProxies.Add(address);
    });
if (options.MockPayments)
{
    bool loopbackOnly = urls.Split(';', StringSplitOptions.RemoveEmptyEntries).All(url =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsLoopback && uri.Scheme is "http" or "https");
    if (!loopbackOnly || trustedProxies.Count > 0 || builder.Configuration.GetSection("Kestrel:Endpoints").GetChildren().Any())
        throw new InvalidOperationException("模拟支付必须仅监听 localhost/127.0.0.1/::1，不能配置额外 Kestrel 端点。");
}
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 34 * 1024 * 1024);
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(new Store(options.DatabasePath));
builder.Services.AddSingleton<AuthService>();
builder.Services.AddHttpClient<IAfdianClient, AfdianClient>(client => client.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddSingleton<PaymentService>();
builder.Services.AddHostedService<StoreMaintenance>();
TranslationEndpoints.AddTranslationServices(builder.Services, builder.Configuration, builder.Environment);
builder.Services.AddRateLimiter(limits =>
{
    limits.RejectionStatusCode = 429;
    limits.OnRejected = async (context, token) =>
    {
        context.HttpContext.Response.StatusCode = 429;
        context.HttpContext.Response.Headers.RetryAfter = "60";
        await context.HttpContext.Response.WriteAsJsonAsync(new { error = "rate_limited", message = "请求过于频繁，请稍后再试。" }, token);
    };
    void Policy(string name, int count, bool perUser = false) => limits.AddPolicy(name, context => RateLimitPartition.GetFixedWindowLimiter(
        (perUser ? context.Items["userId"] as string : null) ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = count, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    Policy("auth", 10); Policy("payment", 20, true); Policy("webhook", 60); Policy("translation-submit", 20, true);
    Policy("redemption", 10, true);
    Policy("translation-cancel", 60, true);
});

var app = builder.Build();
if (trustedProxies.Count > 0) app.UseForwardedHeaders();
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'";
    long limit = context.Request.Method == "PUT" && context.Request.Path.StartsWithSegments("/v1/translations") ? 34 * 1024 * 1024 : 32 * 1024;
    if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } body) body.MaxRequestBodySize = limit;
    try
    {
        if (context.Request.ContentLength > limit) throw new ApiException(413, "request_too_large", "请求内容过大。");
        await next(context);
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
    catch (Exception error)
    {
        if (context.Response.HasStarted) throw;
        var failure = error switch
        {
            ApiException api => api,
            BadHttpRequestException bad => new ApiException(bad.StatusCode, "invalid_request", "请求格式不正确。"),
            JsonException => new ApiException(400, "invalid_request", "请求格式不正确。"),
            _ => new ApiException(500, "server_error", "服务器暂时无法完成请求，请稍后重试。")
        };
        if (failure.StatusCode == 500) app.Logger.LogError("Request failed: {ErrorType}", error.GetType().Name);
        context.Response.StatusCode = failure.StatusCode;
        await context.Response.WriteAsJsonAsync(new { error = failure.Code, message = failure.Message }, context.RequestAborted);
    }
});
app.Use(async (context, next) => { context.RequestServices.GetRequiredService<AuthService>().Authenticate(context); await next(context); });
app.UseRateLimiter();
using var uploads = new SemaphoreSlim(4, 4);
app.Use(async (context, next) =>
{
    if (context.Request.Method != "PUT" || !context.Request.Path.StartsWithSegments("/v1/translations")) { await next(context); return; }
    _ = AuthService.UserId(context);
    if (!uploads.Wait(0)) throw new ApiException(429, "uploads_busy", "上传通道已满，请稍后重试同一请求编号。");
    try { await next(context); }
    finally { uploads.Release(); }
});

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapPost("/v1/auth/register", (Credentials input, AuthService auth) => auth.Register(input)).RequireRateLimiting("auth");
app.MapPost("/v1/auth/login", (Credentials input, AuthService auth) => auth.Login(input)).RequireRateLimiting("auth");
app.MapPost("/v1/auth/logout", (HttpContext context, AuthService auth) => { auth.Logout(context); return Results.NoContent(); });
app.MapGet("/v1/account", (HttpContext context, Store store) => store.GetAccount(AuthService.UserId(context)));
app.MapGet("/v1/ledger", (HttpContext context, Store store) => store.GetLedger(AuthService.UserId(context)));
app.MapPost("/v1/redemptions", (RedeemCodeRequest input, HttpContext context, Store store) =>
    store.RedeemCode(AuthService.UserId(context), input.Code)).RequireRateLimiting("redemption");
app.MapGet("/v1/plans", () => options.Plans.Select(plan => new { plan.Id, plan.Name, plan.Credits, plan.AmountFen }));

object OrderReply(HttpContext context, OrderRecord order)
{
    string? payUrl = order.Status == "pending" && (options.LivePayments || options.MockPayments) ? options.BuildPayUrl(order) : null;
    if (payUrl is not null && options.MockPayments)
    {
        if (!PaymentService.IsLoopback(context)) throw new ApiException(403, "loopback_required", "模拟支付仅允许本机访问。");
        payUrl = context.Request.Scheme + "://" + context.Request.Host + payUrl;
    }
    return new { order.Id, order.Status, payUrl, order.Credits, order.AmountFen, order.CreatedAt };
}
app.MapPost("/v1/orders", (CreateOrderRequest input, HttpContext context, PaymentService payments) =>
    OrderReply(context, payments.CreateOrder(AuthService.UserId(context), input.RequestId, input.PlanId))).RequireRateLimiting("payment");
app.MapGet("/v1/orders", (HttpContext context, Store store) =>
    store.GetOrders(AuthService.UserId(context)).Select(order => OrderReply(context, order)).ToArray());
app.MapGet("/v1/orders/{id}", async (string id, bool? refresh, HttpContext context, Store store, PaymentService payments) =>
{
    string user = AuthService.UserId(context);
    return OrderReply(context, refresh == true ? await payments.RefreshAsync(user, id, context.RequestAborted) : store.GetOrder(user, id));
}).RequireRateLimiting("payment");
app.MapPost("/v1/payments/afdian/webhook", async (HttpContext context, PaymentService payments) =>
{
    using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
    var root = document.RootElement;
    if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
        || !data.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "order"
        || !data.TryGetProperty("order", out var order) || order.ValueKind != JsonValueKind.Object
        || !order.TryGetProperty("out_trade_no", out var number) || number.ValueKind != JsonValueKind.String)
        throw new ApiException(400, "invalid_notification", "订单通知无效。");
    // No amount, identity, plan or success flag from this untrusted body is used.
    await payments.NotifyAsync(number.GetString()!, context.RequestAborted);
    return Results.Json(new { ec = 200, em = "" });
}).RequireRateLimiting("webhook");

if (options.MockPayments)
{
    void LocalOnly(HttpContext context)
    { if (!PaymentService.IsLoopback(context)) throw new ApiException(403, "loopback_required", "模拟支付仅允许本机访问。"); }
    app.MapGet("/dev/payments/{id}", (string id, HttpContext context, Store store) =>
    {
        LocalOnly(context); var order = store.GetMockOrder(id);
        string content = "<!doctype html><html lang=zh><meta charset=utf-8><title>本机模拟支付</title><h1>本机模拟支付</h1><p>"
            + HtmlEncoder.Default.Encode(order.Name) + "：" + order.Credits + " 积分。此页面不收取真实款项。</p>"
            + (order.Status == "paid" ? "<p>模拟到账已完成。</p>" : "<form method=post><input type=hidden name=confirmation value=\""
                + HtmlEncoder.Default.Encode(order.CustomId) + "\"><button type=submit>确认模拟到账</button></form>") + "</html>";
        return Results.Content(content, "text/html; charset=utf-8");
    });
    app.MapPost("/dev/payments/{id}", async (string id, HttpContext context, Store store, PaymentService payments) =>
    {
        LocalOnly(context);
        if (!context.Request.HasFormContentType) throw new ApiException(400, "invalid_confirmation", "请从模拟付款页面确认。");
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        var order = store.GetMockOrder(id);
        if (form["confirmation"].ToString() != order.CustomId) throw new ApiException(403, "invalid_confirmation", "模拟确认信息无效。");
        payments.ConfirmMock(id);
        return Results.Content("<!doctype html><meta charset=utf-8><p>模拟到账完成，可以关闭此页面并返回软件刷新余额。</p>", "text/html; charset=utf-8");
    }).RequireRateLimiting("payment");
}
TranslationEndpoints.MapTranslationEndpoints(app);
app.Run();

public sealed record CreateOrderRequest(string PlanId, string RequestId);
public partial class Program { }
