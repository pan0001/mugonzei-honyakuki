using System.Diagnostics;
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
    private readonly Dictionary<string, Guid> orderRequests = new(StringComparer.Ordinal);
    private readonly string? token = client.Session?.Token;
    private bool busy, disposed;

    private bool Current
    {
        get
        {
            try { return !disposed && token != null && client.Session is { IsExpired: false } session && session.Token == token; }
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
        if (action is not ("ready" or "refresh" or "redeem" or "buy" or "pay" or "verify" or "recover" or "cancel" or "logout")) return;
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
                case "buy": await Purchase(Field(message, "id", 128)); break;
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
            if (error is CreditServiceException { StatusCode: System.Net.HttpStatusCode.Unauthorized } && token != null)
                client.ClearLocalSession(token);
            if (action == "logout") signOutNotice = "已清除本机登录。服务器未确认退出请求。";
            else Notice(error is InvalidOperationException ? error.Message : "操作未完成，请稍后重试。");
        }
        finally
        {
            busy = false;
            if (!disposed)
            {
                if (!Current) Send(new { type = "signedOut", message = signOutNotice ?? "登录已过期或账户已更改。" });
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

    private void SendOrders() => Send(new { type = "orders", rows = orders.Values.OrderByDescending(r => r.CreatedAt)
        .Select(r => new { r.Id, r.Status, r.CreatedAt, r.Credits, r.AmountFen, payable = r.Status == "pending" && r.PayUrl?.Length > 0 }) });

    private CreditOrder FindOrder(JsonElement message) => orders.TryGetValue(Field(message, "id", 128), out var order)
        ? order : throw new InvalidOperationException("请刷新后选择一个订单。");

    private async Task Purchase(string plan)
    {
        if (!plans.ContainsKey(plan)) throw new InvalidOperationException("请刷新后选择一个积分包。");
        if (!orderRequests.TryGetValue(plan, out var id)) orderRequests[plan] = id = Guid.NewGuid();
        var order = await client.CreateOrderAsync(plan, id, lifetime.Token);
        if (!Current) return;
        orderRequests.Remove(plan);
        orders[order.Id] = order;
        SendOrders();
        if (order.Status == "pending") OpenPayment(order);
        else Notice("订单已更新，请查看订单记录。");
    }

    private void OpenPayment(CreditOrder order)
    {
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
        _ = ReleaseAsync();
    }

    private async Task ReleaseAsync()
    {
        await BackgroundCancellation.RequestAsync(lifetime).ConfigureAwait(false);
        client.Dispose();
        lifetime.Dispose();
    }
}
