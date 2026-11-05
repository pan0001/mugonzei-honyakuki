using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mugonzei.Server;

public sealed record VerifiedAfdianOrder(string TradeNo, string CustomId, string PlanId, int AmountFen, int Status);
public interface IAfdianClient
{
    Task<IReadOnlyList<VerifiedAfdianOrder>> QueryAsync(string? tradeNo, int page, CancellationToken token);
}

public sealed class AfdianClient(HttpClient http, ServerOptions options) : IAfdianClient
{
    public async Task<IReadOnlyList<VerifiedAfdianOrder>> QueryAsync(string? tradeNo, int page, CancellationToken token)
    {
        if (!options.LivePayments) throw new ApiException(503, "payments_disabled", "真实收款尚未启用。");
        string parameters = tradeNo is null ? JsonSerializer.Serialize(new { page }) : JsonSerializer.Serialize(new { out_trade_no = tradeNo });
        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string signature = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(options.AfdianToken
            + "params" + parameters + "ts" + timestamp.ToString(CultureInfo.InvariantCulture) + "user_id" + options.CreatorId))).ToLowerInvariant();
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://afdian.com/api/open/query-order")
        {
            Content = JsonContent.Create(new { user_id = options.CreatorId, @params = parameters, ts = timestamp, sign = signature })
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is > 1_048_576)
                throw Unavailable();
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var data = new MemoryStream(); byte[] buffer = new byte[8192]; int count;
            while ((count = await stream.ReadAsync(buffer, timeout.Token)) != 0)
            {
                if (data.Length + count > 1_048_576) throw Unavailable();
                data.Write(buffer, 0, count);
            }
            using var document = JsonDocument.Parse(data.ToArray());
            var root = document.RootElement;
            if (!root.TryGetProperty("ec", out var ec) || ec.GetInt32() != 200
                || !root.TryGetProperty("data", out var result) || !result.TryGetProperty("list", out var list)
                || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > 100) throw Unavailable();
            var orders = new List<VerifiedAfdianOrder>();
            foreach (var item in list.EnumerateArray())
            {
                string Field(string key) => item.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString()! : "";
                string orderNo = Field("out_trade_no");
                if (tradeNo is not null && orderNo != tradeNo) continue;
                string customId = Field("custom_order_id");
                if (orderNo.Length is < 1 or > 128 || customId.Length is < 1 or > 128) continue;
                if (!decimal.TryParse(Field("total_amount"), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal amount)
                    || amount <= 0 || amount > 100_000m || decimal.Truncate(amount * 100) != amount * 100) continue;
                if (!item.TryGetProperty("status", out var status) || !status.TryGetInt32(out int state)) continue;
                orders.Add(new(orderNo, customId, Field("plan_id"), (int)(amount * 100), state));
            }
            return orders;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw Unavailable(); }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException or InvalidOperationException or FormatException)
        { throw Unavailable(); }
    }
    private static ApiException Unavailable() => new(502, "payment_verification_unavailable", "暂时无法核验支付订单，请稍后刷新；未增加或扣除积分。");
}

public sealed class PaymentService(Store store, ServerOptions options, IAfdianClient afdian, TimeProvider? clock = null)
{
    // One in-flight refresh per order. Entries are removed on completion, so this
    // guard cannot grow with the lifetime order count.
    private readonly ConcurrentDictionary<string, byte> refreshing = new();

    private void RequireCheckout()
    {
        if (!options.LivePayments && !options.MockPayments)
            throw new ApiException(503, "payments_disabled", "充值尚未开放。");
    }

    public PriceQuote Quote(string planId, string? couponCode = null)
    {
        RequireCheckout();
        return store.Quote(options.Plans, planId, couponCode);
    }

    public OrderRecord CreateOrder(string userId, string requestId, string planId, string? couponCode = null, int? expectedAmountFen = null)
    {
        RequireCheckout();
        return store.CreateOrder(userId, requestId, options.Plans, planId, couponCode, expectedAmountFen);
    }

    public async Task<OrderRecord> RefreshAsync(string userId, string id, CancellationToken token)
    {
        var order = store.GetOrder(userId, id);
        if (order.Status != "pending" || options.MockPayments) return order;
        if (!options.LivePayments) throw new ApiException(503, "payments_disabled", "真实收款尚未启用。");
        if (!refreshing.TryAdd(order.Id, 0)) throw new ApiException(429, "refresh_in_progress", "订单正在核验，请稍后刷新。");
        try
        {
            // Bounded reconciliation for recent missed callbacks. A callback can
            // verify any older order directly by its provider transaction number.
            for (int page = 1; page <= 3; page++)
            {
                var orders = await afdian.QueryAsync(null, page, token);
                foreach (var verified in orders.Where(p => p.CustomId == order.CustomId)) ApplyVerified(verified);
                order = store.GetOrder(userId, id);
                // The client filters unrelated/zero-value provider entries. An
                // empty filtered page does not mean the provider has no next page.
                if (order.Status != "pending") return order;
            }
            return order;
        }
        finally { refreshing.TryRemove(order.Id, out _); }
    }

    public void AcceptNotification(string tradeNo)
    {
        if (!options.LivePayments) throw new ApiException(503, "payments_disabled", "真实收款尚未启用。");
        if (tradeNo.Length is < 1 or > 128 || tradeNo.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            throw new ApiException(400, "invalid_notification", "订单通知无效。");
        store.EnqueuePaymentVerification(tradeNo, (clock ?? TimeProvider.System).GetUtcNow());
    }

    internal async Task<bool> VerifyNotificationAsync(string tradeNo, CancellationToken token)
    {
        foreach (var order in await afdian.QueryAsync(tradeNo, 1, token))
        {
            if (order.TradeNo != tradeNo || order.Status != 2) continue;
            // A verified completed purchase may belong to another product at this creator.
            // Only an existing local custom ID and matching price and plan can award credits.
            ApplyVerified(order);
            return true;
        }
        return false;
    }

    public OrderRecord? ApplyVerified(VerifiedAfdianOrder payment)
    {
        if (payment.Status != 2) return null;
        var order = store.FindOrderByCustomId(payment.CustomId);
        if (order is null) return null;
        return store.CreditOrder(order.Id, payment.TradeNo, payment.AmountFen, payment.PlanId, payment.CustomId);
    }

    public OrderRecord ConfirmMock(string id)
    {
        if (!options.MockPayments) throw new ApiException(404, "not_found", "页面不存在。");
        // id is an unguessable local test checkout identifier, not a production
        // authentication substitute. The HTTP route also enforces loopback.
        var order = store.GetMockOrder(id);
        return ApplyVerified(new("mock-" + order.Id, order.CustomId, order.AfdianPlanId, order.AmountFen, 2))!;
    }

    public static bool IsLoopback(HttpContext context) => context.Connection.RemoteIpAddress is { } address
        && IPAddress.IsLoopback(address) && Uri.TryCreate(context.Request.Scheme + "://" + context.Request.Host, UriKind.Absolute, out var uri) && uri.IsLoopback;
}
