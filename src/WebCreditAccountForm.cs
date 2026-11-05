using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SteamVrTranslator;

internal sealed class WebCreditAccountForm : Form
{
    private readonly WebCreditAccount account;

    internal WebCreditAccountForm(string serverUrl)
    {
        Text = "无言势翻译器 · 账号与积分";
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = UiTheme.Background;
        Size = new Size(840, 670);
        MinimumSize = new Size(480, 440);
        StartPosition = FormStartPosition.CenterParent;
        var web = new LocalWebSurface("account.html", Receive);
        account = new WebCreditAccount(new CreditServiceClient(serverUrl), web.Post,
            uri => Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }));
        Controls.Add(web);
        WindowChrome.Apply(this);
    }

    private Task Receive(JsonElement message) => account.Receive(message);

    protected override void Dispose(bool disposing)
    {
        if (disposing) account?.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>The account and checkout URLs stay native. The page addresses displayed plans and orders only by ID.</summary>
internal sealed class WebCreditAccount(CreditServiceClient client, Action<object> post, Action<Uri> open) : IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<string, CreditPlan> plans = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CreditOrder> orders = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Plan, string? Coupon, long? Price), Guid> orderRequests = new();
    private sealed record ConfirmedQuote(string Id, string Coupon, CreditCheckoutQuote Price);
    private ConfirmedQuote? offered;
    private readonly CreditSession? session = client.Session;
    // An account window can reopen during the same login. Keep only suspect order IDs,
    // scoped to a fingerprint of the endpoint and token; nothing is persisted.
    private static readonly ConcurrentDictionary<(string Scope, string Id), DateTimeOffset> heldPayments = new();
    private string? paymentScope;
    private string PaymentScope => paymentScope ??= Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(client.ServerUrl + "\n" + session?.Token)));
    private bool busy, disposed;

    private bool Current
    {
        get
        {
            try { return !disposed && session != null && client.Session is { IsExpired: false } stored && stored.Token == session.Token; }
            catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException) { return false; }
        }
    }
    private void Send(object value) { if (!disposed) post(value); }
    private void Notice(string text) => Send(new { type = "notice", text });

    internal async Task Receive(JsonElement message)
    {
        if (disposed || busy || message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String) return;
        string? action = type.GetString();
        if (action is not ("ready" or "refresh" or "redeem" or "buy" or "quote" or "confirmCoupon" or "pay" or "verify" or "recover" or "cancel" or "logout")) return;
        busy = true;
        string? signOutNotice = null;
        try
        {
            if (!Current) return;
            Send(new { type = "busy", value = true });
            switch (action)
            {
                case "ready":
                    Send(new { type = "identity", username = client.Session!.Username, host = new Uri(client.ServerUrl).Authority });
                    goto case "refresh";
                case "refresh":
                    await Task.WhenAll(RefreshBalance(), RefreshPlans(), RefreshOrders(), RefreshLedger());
                    if (Current) Notice("已刷新。");
                    break;
                case "redeem":
                    var result = await client.RedeemAsync(Field(message, "code", 128), lifetime.Token);
                    if (!Current) break;
                    Send(new { type = "balance", balance = result.Balance, reserved = result.Reserved });
                    string confirmation = result.AlreadyRedeemed ? "这个兑换码已计入你的余额。" : $"已兑换 {result.Credits} 积分。";
                    try { await RefreshLedger(); }
                    catch (Exception) when (Current) { confirmation += " 稍后刷新以查看流水。"; }
                    if (Current) Notice(confirmation);
                    break;
                case "buy":
                    if (message.TryGetProperty("couponCode", out _)) throw new InvalidOperationException("请先查看并确认优惠价。");
                    offered = null;
                    await Purchase(Field(message, "id", 128));
                    break;
                case "quote":
                    offered = null;
                    var planId = Field(message, "id", 128);
                    if (!plans.TryGetValue(planId, out var plan)) throw new InvalidOperationException("请刷新后选择一个积分包。");
                    var coupon = Field(message, "couponCode", 80).Trim();
                    var quote = await client.QuoteAsync(planId, coupon, lifetime.Token);
                    if (!Current) break;
                    offered = new(Guid.NewGuid().ToString("N"), coupon, quote);
                    Send(new { type = "quote", id = offered.Id, plan.Name, quote.OriginalAmountFen, quote.DiscountFen, quote.AmountFen, quote.Credits });
                    break;
                case "confirmCoupon":
                    if (offered is not { } offer || Field(message, "id", 128) != offer.Id || Field(message, "couponCode", 80).Trim() != offer.Coupon)
                        throw new InvalidOperationException("请先查看并确认当前优惠价。");
                    await Purchase(offer.Price.PlanId, offer);
                    break;
                case "pay": OpenPayment(FindOrder(message)); break;
                case "verify":
                    var order = FindOrder(message);
                    var updated = await client.GetOrderAsync(order.Id, true, lifetime.Token);
                    if (!Current) break;
                    if (updated.Id != order.Id) throw new InvalidOperationException("服务返回的订单编号不一致，请刷新订单。");
                    orders[order.Id] = updated;
                    SendOrders();
                    await Task.WhenAll(RefreshBalance(), RefreshLedger());
                    if (Current) Notice(updated.Status is "paid" or "completed" ? "付款已核验，积分已到账。" : "订单已核验，请查看最新状态。");
                    break;
                case "recover": await Recent(false); break;
                case "cancel": await Recent(true); break;
                case "logout":
                    await client.LogoutAsync(lifetime.Token);
                    signOutNotice = "已退出账号。";
                    break;
            }
        }
        catch (Exception error)
        {
            if (disposed) return;
            if (error is CreditServiceException { StatusCode: System.Net.HttpStatusCode.Unauthorized } && session != null)
                client.ClearLocalSession(session.Token);
            if (error is CreditOrderReviewException suspect && session != null)
            {
                foreach (var (key, expiresAt) in heldPayments)
                    if (expiresAt <= DateTimeOffset.UtcNow) heldPayments.TryRemove(key, out _);
                heldPayments[(PaymentScope, suspect.OrderId)] = session.ExpiresAt;
                if (Current) SendOrders();
            }
            if (action == "logout") signOutNotice = "已清除本机登录。服务器未确认退出请求。";
            else
            {
                if (action == "confirmCoupon" && error is not CreditServiceException { IsTransient: true }) ClearQuote();
                Notice(error is InvalidOperationException ? error.Message : "操作未完成，请稍后重试。");
            }
        }
        finally
        {
            busy = false;
            if (!disposed)
            {
                if (!Current)
                {
                    if (signOutNotice != null && !heldPayments.IsEmpty)
                        foreach (var key in heldPayments.Keys)
                            if (key.Scope == PaymentScope) heldPayments.TryRemove(key, out _);
                    offered = null;
                    orderRequests.Clear();
                    Send(new { type = "signedOut", message = signOutNotice ?? "登录已过期或账户已更改。" });
                }
                Send(new { type = "busy", value = false });
            }
        }
    }

    private static string Field(JsonElement message, string field, int limit)
    {
        if (!message.TryGetProperty(field, out var item) || item.ValueKind != JsonValueKind.String ||
            item.GetString() is not { Length: > 0 } value || value.Length > limit || value.Any(char.IsControl))
            throw new InvalidOperationException("输入无效，请重新选择或填写。");
        return value;
    }

    private async Task RefreshBalance()
    {
        var account = await client.GetAccountAsync(lifetime.Token);
        if (Current) Send(new { type = "balance", account.Balance, account.Reserved });
    }

    private async Task RefreshPlans()
    {
        var rows = await client.GetPlansAsync(lifetime.Token);
        if (!Current) return;
        offered = null;
        plans.Clear();
        foreach (var row in rows.Take(100)) plans[row.Id] = row;
        Send(new { type = "plans", rows = plans.Values.Select(p => new { p.Id, p.Name, p.Credits, p.AmountFen }) });
    }

    private async Task RefreshLedger()
    {
        var rows = await client.GetLedgerAsync(lifetime.Token);
        if (Current) Send(new { type = "ledger", rows = rows.Take(1000).Select(r => new { r.CreatedAt, r.Kind, r.Amount, r.Balance, r.Reference }) });
    }

    private async Task RefreshOrders()
    {
        var rows = await client.GetOrdersAsync(lifetime.Token);
        if (!Current) return;
        orders.Clear();
        foreach (var row in rows.Take(1000)) orders[row.Id] = row;
        SendOrders();
    }

    private bool PaymentHeld(string id) => !heldPayments.IsEmpty && heldPayments.ContainsKey((PaymentScope, id));

    private void SendOrders() => Send(new { type = "orders", rows = orders.Values.OrderByDescending(r => r.CreatedAt)
        .Select(r => new { r.Id, r.Status, r.CreatedAt, r.Credits, r.AmountFen, paymentHeld = PaymentHeld(r.Id),
            payable = r.Status == "pending" && r.PayUrl?.Length > 0 && !PaymentHeld(r.Id) }) });

    private CreditOrder FindOrder(JsonElement message) => orders.TryGetValue(Field(message, "id", 128), out var order)
        ? order : throw new InvalidOperationException("请刷新后选择一个订单。");

    private void ClearQuote(bool purchased = false)
    {
        offered = null;
        Send(new { type = "clearQuote", purchased });
    }

    private async Task Purchase(string plan, ConfirmedQuote? offer = null)
    {
        if (!plans.ContainsKey(plan)) throw new InvalidOperationException("请刷新后选择一个积分包。");
        var key = (plan, offer?.Coupon, offer?.Price.AmountFen);
        if (!orderRequests.TryGetValue(key, out var id)) orderRequests[key] = id = Guid.NewGuid();
        var order = offer == null ? await client.CreateOrderAsync(plan, id, lifetime.Token)
            : await client.CreateCouponOrderAsync(offer.Price, offer.Coupon, id, lifetime.Token);
        if (!Current) return;
        orderRequests.Remove(key);
        if (offer != null) ClearQuote(purchased: true);
        orders[order.Id] = order;
        SendOrders();
        if (order.Status == "pending") OpenPayment(order);
        else Notice("订单已更新，请查看订单记录。");
    }

    private void OpenPayment(CreditOrder order)
    {
        if (PaymentHeld(order.Id)) throw new InvalidOperationException("该订单已阻止付款，请联系运营者核对。");
        if (order.Status != "pending" || string.IsNullOrWhiteSpace(order.PayUrl))
            throw new InvalidOperationException("这个订单当前无法付款。");
        open(CreditServiceClient.ValidatePaymentUrl(order.PayUrl, client.ServerUrl));
        Notice("已打开付款页面。付款后点击该订单的「核验到账」。");
    }

    private async Task Recent(bool cancel)
    {
        var id = client.Session?.RecentTaskId ?? throw new InvalidOperationException("没有可以恢复的最近任务。");
        var result = cancel ? await client.CancelAsync(id, lifetime.Token) : await client.ResumeLatestAsync(lifetime.Token);
        if (!Current) return;
        Send(new { type = "recent", result.Status, result.IsExpired, result.SourceText, result.Translation, result.Error });
        Notice(cancel ? "已查询取消结果。" : "已读取最近任务。");
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        offered = null;
        orderRequests.Clear();
        _ = ReleaseAsync();
    }

    private async Task ReleaseAsync()
    {
        await BackgroundCancellation.RequestAsync(lifetime).ConfigureAwait(false);
        client.Dispose();
        lifetime.Dispose();
    }
}
