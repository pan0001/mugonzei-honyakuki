using System.Diagnostics;

namespace SteamVrTranslator;

/// <summary>All network activity is initiated by a button. Opening or rendering this window is offline.</summary>
public sealed class CreditAccountForm : Form
{
    private readonly Func<string, CreditServiceClient> createClient;
    private readonly TextBox endpoint = Input("creditAccountServer"), username = Input("creditUsername"), password = Input("creditPassword");
    private readonly TextBox email = Input("creditRegistrationEmail"), verificationCode = Input("creditRegistrationCode");
    private readonly TableLayoutPanel registrationFields = Stack();
    private readonly System.Windows.Forms.Timer resendTimer = new() { Interval = 500 };
    private Button sendCode = null!, submitAuth = null!, switchAuth = null!;
    private bool registering;
    private long resendAfter;
    private string? codeRequestedFor, resendAddress;
    private readonly TextBox redemptionCode = Input("creditRedemptionCode");
    private readonly Label status = Note("登录后可查看余额、购买次数包和恢复最近译文。"), balance = Note("尚未刷新账户");
    private readonly ComboBox plans = new UiComboBox { Name = "creditPlans", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly ListView ledger = Table("creditLedger", ("时间", 150), ("类型", 110), ("变动", 80), ("余额", 80), ("关联编号", 300));
    private readonly ListView orders = Table("creditOrders", ("时间", 150), ("状态", 110), ("积分", 80), ("金额", 90), ("订单编号", 280));
    private readonly TextBox recent = new() { Name = "creditRecentTranslation", Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
        Dock = DockStyle.Fill, BackColor = UiTheme.Input, ForeColor = UiTheme.Ink, BorderStyle = BorderStyle.None };
    private readonly List<Button> actionButtons = [];
    private readonly CancellationTokenSource lifetime = new();
    private Task? lifetimeCancellation;
    private CreditServiceClient? client;
    private bool working, closing, resourcesDisposed;
    private Guid? pendingOrderRequest;
    private string? pendingPlan;
    public string ServerUrl { get; private set; }

    public CreditAccountForm(string serverUrl) : this(serverUrl, url => new CreditServiceClient(url)) { }
    internal CreditAccountForm(string serverUrl, Func<string, CreditServiceClient> createClient)
    {
        ServerUrl = CreditServiceClient.NormalizeServerUrl(serverUrl);
        this.createClient = createClient;
        Text = "无言势翻译器 · 积分账户";
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        Font = new Font("Microsoft YaHei UI", 10);
        BackColor = UiTheme.Background; ForeColor = UiTheme.Ink;
        Size = new Size(1020, 820); MinimumSize = new Size(820, 660);
        StartPosition = FormStartPosition.CenterParent;
        endpoint.Text = ServerUrl; password.UseSystemPasswordChar = true;
        username.PlaceholderText = "3–32 位字母、数字、下划线或连字符"; password.PlaceholderText = "10–128 个字符；密码不会保存到本机";
        email.PlaceholderText = "name@example.com"; email.MaxLength = 254;
        email.AccessibleName = "注册邮箱"; verificationCode.AccessibleName = "邮箱验证码";
        verificationCode.PlaceholderText = "邮件中的 6 位数字"; verificationCode.MaxLength = 6;
        verificationCode.UseSystemPasswordChar = true;
        redemptionCode.PlaceholderText = "输入完整 MU 兑换码或运营者发放的兑换口令";
        redemptionCode.MaxLength = 128; redemptionCode.UseSystemPasswordChar = true;
        BuildUi();
        WindowChrome.Apply(this);
        email.TextChanged += (_, _) => { verificationCode.Clear(); UpdateRegistrationControls(); };
        verificationCode.TextChanged += (_, _) => UpdateRegistrationControls();
        resendTimer.Tick += (_, _) => UpdateRegistrationControls();
        FormClosing += (_, _) => { closing = true; _ = CancelLifetimeAsync(); };
    }

    private void BuildUi()
    {
        var shell = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, RowCount = 4 };
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.AutoSize)); shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(shell);
        var heading = Note("积分账户"); heading.Font = new Font(Font.FontFamily, 23, FontStyle.Bold); heading.ForeColor = UiTheme.Ink;
        shell.Controls.Add(heading, 0, 0);
        var address = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 2, Margin = new Padding(0, 10, 0, 18) };
        address.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); address.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        address.Controls.Add(Frame(endpoint), 0, 0);
        address.Controls.Add(ActionButton("使用此服务地址", () => { ApplyEndpoint(); return Task.CompletedTask; }), 1, 0);
        shell.Controls.Add(address, 0, 1);
        var section = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
        section.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        section.RowStyles.Add(new RowStyle(SizeType.AutoSize)); section.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var navigation = Flow(); navigation.Margin = new Padding(0, 0, 0, 10);
        var tabs = new UiPages { Name = "creditAccountTabs", Dock = DockStyle.Fill };
        section.Controls.Add(navigation, 0, 0); section.Controls.Add(tabs, 0, 1);
        shell.Controls.Add(section, 0, 2);
        BuildAccountTab(tabs); BuildPurchaseTab(tabs); BuildLedgerTab(tabs); BuildRecoveryTab(tabs);
        for (int index = 0; index < tabs.TabPages.Count; index++)
        {
            int selectedIndex = index;
            var button = new UiButton { Text = tabs.TabPages[index].Text, AutoSize = true,
                Name = "creditNavigation" + index, Selected = index == 0, Margin = new Padding(0, 0, 8, 0) };
            button.Click += (_, _) => tabs.SelectedIndex = selectedIndex;
            tabs.SelectedIndexChanged += (_, _) => button.Selected = tabs.SelectedIndex == selectedIndex;
            navigation.Controls.Add(button);
        }
        status.Dock = DockStyle.Fill; status.Padding = new Padding(0, 14, 0, 0); status.ForeColor = UiTheme.Accent;
        status.MaximumSize = new Size(900, 0); shell.Controls.Add(status, 0, 3);
        shell.SizeChanged += (_, _) => status.MaximumSize = new Size(Math.Max(200, shell.ClientSize.Width - shell.Padding.Horizontal), 0);
    }

    private void BuildAccountTab(TabControl tabs)
    {
        var page = Page(tabs, "登录与余额"); var stack = Stack(); page.Controls.Add(stack);
        Add(stack, Note("已有账号用用户名和密码登录。新账号需验证邮箱。"));
        Add(stack, Note("用户名 · 3–32 位英文字母、数字、下划线或短横线")); Add(stack, Frame(username));
        Add(stack, Note("密码 · 10–128 个字符，不保存到本机")); Add(stack, Frame(password));
        Add(registrationFields, Note("注册邮箱"));
        var delivery = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 2, Margin = Padding.Empty };
        delivery.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); delivery.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        delivery.Controls.Add(Frame(email), 0, 0);
        sendCode = ActionButton("发送验证码", RequestCode);
        sendCode.Name = "creditSendEmailCode"; delivery.Controls.Add(sendCode, 1, 0);
        Add(registrationFields, delivery);
        Add(registrationFields, Note("邮箱验证码 · 10 分钟内有效")); Add(registrationFields, Frame(verificationCode));
        Add(stack, registrationFields); registrationFields.Visible = false;
        var actions = Flow();
        submitAuth = ActionButton("登录", Authenticate, primary: true); submitAuth.Name = "creditAuthSubmit";
        switchAuth = ActionButton("注册新账户", () => { SetRegistrationMode(!registering); return Task.CompletedTask; });
        switchAuth.Name = "creditAuthSwitch"; actions.Controls.Add(submitAuth); actions.Controls.Add(switchAuth);
        actions.Controls.Add(ActionButton("退出登录", Logout));
        actions.Controls.Add(ActionButton("清除本地登录", () => { EnsureClient().ClearLocalSession(); ClearDisplays(); status.Text = "本地登录已清除。服务器上的会话将按其有效期失效。"; return Task.CompletedTask; }));
        Add(stack, actions); Add(stack, balance);
        Add(stack, ActionButton("刷新余额、流水与订单", RefreshAll));
        Add(stack, Note("兑换码"));
        var redemption = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Margin = Padding.Empty };
        redemption.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); redemption.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        redemption.Controls.Add(Frame(redemptionCode), 0, 0);
        var redeemButton = ActionButton("兑换积分", Redeem, primary: true); redeemButton.Name = "creditRedeemButton";
        redemption.Controls.Add(redeemButton, 1, 0); Add(stack, redemption);
        Add(stack, Note("一次性码只有一个账号能领，共享码每个账号都可领一次。网页和桌面使用同一账号时共享积分余额。"));
        Add(stack, Note("只有完整翻译成功才扣费；任务运行时可能预留积分。取消和断线后的最终结果以服务端记录为准。\n账户窗口中修改的服务地址会带回主界面，请在主界面保存设置后用于翻译。"));
    }

    private void BuildPurchaseTab(TabControl tabs)
    {
        var page = Page(tabs, "次数包与订单");
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 4; i++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); page.Controls.Add(layout);
        layout.Controls.Add(Note("先登录并刷新账户，再选择次数包。付款会在外部浏览器完成；请勿重复支付同一订单。"), 0, 0);
        plans.Margin = new Padding(0, 10, 0, 12); layout.Controls.Add(plans, 0, 1);
        var actions = Flow();
        actions.Controls.Add(ActionButton("购买所选次数包", Purchase, primary: true));
        actions.Controls.Add(ActionButton("打开所选订单支付页", OpenSelectedPayment));
        actions.Controls.Add(ActionButton("刷新所选订单", RefreshSelectedOrder));
        actions.Controls.Add(ActionButton("刷新订单列表", RefreshOrders));
        layout.Controls.Add(actions, 0, 2);
        layout.Controls.Add(Note("支付页打开或返回客户端不表示到账。请刷新订单，到账以服务端确认后的积分余额为准。"), 0, 3);
        layout.Controls.Add(orders, 0, 4);
    }

    private void BuildLedgerTab(TabControl tabs)
    {
        var page = Page(tabs, "积分流水"); page.Controls.Add(ledger);
    }

    private void BuildRecoveryTab(TabControl tabs)
    {
        var page = Page(tabs, "恢复最近译文");
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        page.Controls.Add(layout);
        layout.Controls.Add(Note("查询同一服务、同一账户保存的最近任务编号，不创建新任务，也不会再次扣费。\n恢复的原文和译文仅显示在这里，不会自动发送到 VR 字幕。"), 0, 0);
        var actions = Flow();
        actions.Controls.Add(ActionButton("查询 / 恢复最近任务", Recover));
        actions.Controls.Add(ActionButton("取消最近任务", CancelRecent));
        layout.Controls.Add(actions, 0, 1); recent.Margin = new Padding(0, 12, 0, 0); layout.Controls.Add(recent, 0, 2);
    }

    private CreditServiceClient EnsureClient()
    {
        if (CreditServiceClient.NormalizeServerUrl(endpoint.Text) != ServerUrl)
            throw new InvalidOperationException("服务地址尚未应用，请先点击“使用此服务地址”。");
        return client ??= createClient(ServerUrl);
    }

    private void ApplyEndpoint()
    {
        string changed = CreditServiceClient.NormalizeServerUrl(endpoint.Text);
        if (changed != ServerUrl)
        {
            client?.Dispose(); client = null; ServerUrl = changed; password.Clear(); ClearDisplays();
            ResetRegistration();
            pendingOrderRequest = null; pendingPlan = null;
        }
        endpoint.Text = ServerUrl;
        status.Text = "服务地址已应用。请登录或刷新已有登录；此操作没有连接服务。";
    }

    private void SetRegistrationMode(bool create)
    {
        registering = create;
        registrationFields.Visible = create;
        submitAuth.Text = create ? "验证并注册" : "登录";
        switchAuth.Text = create ? "返回登录" : "注册新账户";
        if (!create) { verificationCode.Clear(); email.Clear(); }
        status.Text = create ? "输入邮箱并点击“发送验证码”，然后填写收到的 6 位数字。" : "使用已有账号的用户名和密码登录。";
        UpdateRegistrationControls();
    }

    private async Task RequestCode()
    {
        var service = EnsureClient();
        string address = email.Text.Trim();
        try { await service.RequestRegistrationCodeAsync(address, lifetime.Token); }
        catch (CreditServiceException error) when (!closing && error.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        { StartResendCooldown(address); throw; }
        if (closing) return;
        codeRequestedFor = address;
        StartResendCooldown(address);
        verificationCode.Clear();
        status.Text = "请查看收件箱或垃圾邮件，填写最新验证码。验证码 10 分钟内有效。";
    }

    private void StartResendCooldown(string address)
    {
        resendAddress = address;
        resendAfter = Environment.TickCount64 + 60_000;
        resendTimer.Start();
        UpdateRegistrationControls();
    }

    private void UpdateRegistrationControls()
    {
        string address = email.Text.Trim();
        int seconds = (int)Math.Clamp((resendAfter - Environment.TickCount64 + 999) / 1000, 0, 60);
        if (seconds == 0) resendTimer.Stop();
        bool waiting = seconds > 0 && string.Equals(resendAddress, address, StringComparison.OrdinalIgnoreCase);
        sendCode.Text = waiting ? $"重新发送（{seconds} 秒）" : "发送验证码";
        sendCode.Enabled = !working && !waiting && address.Length > 0;
        submitAuth.Enabled = !working && (!registering || (codeRequestedFor is not null
            && string.Equals(codeRequestedFor, address, StringComparison.OrdinalIgnoreCase)
            && verificationCode.Text.Length == 6 && verificationCode.Text.All(char.IsAsciiDigit)));
    }

    private void ResetRegistration()
    {
        codeRequestedFor = resendAddress = null;
        resendAfter = 0;
        SetRegistrationMode(false);
    }

    private async Task Authenticate()
    {
        var service = EnsureClient();
        CreditSession session;
        try
        {
            session = registering
                ? await service.RegisterAsync(username.Text, password.Text, email.Text, verificationCode.Text, lifetime.Token)
                : await service.LoginAsync(username.Text, password.Text, lifetime.Token);
        }
        finally { if (!closing && !registering) password.Clear(); }
        if (closing) return;
        password.Clear(); ResetRegistration();
        ClearDisplays(); username.Text = session.Username; pendingOrderRequest = null; pendingPlan = null;
        status.Text = "登录成功，正在读取账户…";
        await RefreshAll();
    }

    private async Task Logout()
    {
        try { await EnsureClient().LogoutAsync(lifetime.Token); }
        finally { if (!closing) { password.Clear(); ClearDisplays(); pendingOrderRequest = null; pendingPlan = null; } }
        if (!closing) status.Text = "已退出当前积分账户。";
    }

    private async Task RefreshAll()
    {
        var service = EnsureClient();
        var session = service.Session;
        // Each response updates only its own view. A slow or failed plans/order
        // request must not hide an account balance that has already arrived.
        await Task.WhenAll(RefreshBalance(service, session), RefreshLedger(service, session),
            RefreshPlans(service, session), RefreshOrders(service, session));
        if (IsCurrentSession(service, session)) status.Text = "账户已刷新。";
    }

    private async Task RefreshBalanceAndLedger(CreditServiceClient service, CreditSession? session) =>
        await Task.WhenAll(RefreshBalance(service, session), RefreshLedger(service, session));

    private async Task RefreshBalance(CreditServiceClient service, CreditSession? session)
    {
        var account = await service.GetAccountAsync(lifetime.Token);
        if (!IsCurrentSession(service, session)) return;
        balance.Text = $"账户：{account.Username}    总余额：{account.Balance}    预留：{account.Reserved}    可用：{Math.Max(0, account.Balance - account.Reserved)} 积分";
    }

    private async Task RefreshLedger(CreditServiceClient service, CreditSession? session)
    {
        var rows = await service.GetLedgerAsync(lifetime.Token);
        if (!IsCurrentSession(service, session)) return;
        ledger.BeginUpdate();
        try
        {
            ledger.Items.Clear();
            foreach (var row in rows.Take(1000)) ledger.Items.Add(new ListViewItem([
                row.CreatedAt.ToLocalTime().ToString("MM-dd HH:mm:ss"), row.Kind == "redemption" ? "兑换码入账" : row.Kind,
                row.Amount.ToString("+0;-0;0"), row.Balance.ToString(), row.Reference]));
        }
        finally { ledger.EndUpdate(); }
    }

    private async Task RefreshPlans(CreditServiceClient service, CreditSession? session)
    {
        var rows = await service.GetPlansAsync(lifetime.Token);
        if (!IsCurrentSession(service, session)) return;
        string? selected = (plans.SelectedItem as CreditPlan)?.Id;
        plans.BeginUpdate();
        try
        {
            plans.Items.Clear();
            foreach (var plan in rows.Take(100)) plans.Items.Add(plan);
            plans.SelectedIndex = plans.Items.Cast<CreditPlan>().Select((p, index) => (p, index)).FirstOrDefault(x => x.p.Id == selected, (null!, -1)).index;
            if (plans.SelectedIndex < 0 && plans.Items.Count > 0) plans.SelectedIndex = 0;
        }
        finally { plans.EndUpdate(); }
    }

    private async Task RefreshOrders(CreditServiceClient service, CreditSession? session)
    {
        var rows = await service.GetOrdersAsync(lifetime.Token);
        if (IsCurrentSession(service, session)) PopulateOrders(rows);
    }

    private async Task RefreshOrders()
    {
        var service = EnsureClient(); var session = service.Session;
        await RefreshOrders(service, session);
        if (IsCurrentSession(service, session)) status.Text = "订单列表已刷新。";
    }

    private bool IsCurrentSession(CreditServiceClient service, CreditSession? session) => !closing
        && !lifetime.IsCancellationRequested && ReferenceEquals(service, client) && session is not null
        && service.Session?.Token == session.Token;

    private async Task Redeem()
    {
        var service = EnsureClient();
        var session = service.Session;
        string submittedCode = redemptionCode.Text;
        redemptionCode.Clear();
        var result = await service.RedeemAsync(submittedCode, lifetime.Token);
        if (!IsCurrentSession(service, session)) return;
        balance.Text = $"账户：{session!.Username}    总余额：{result.Balance}    预留：{result.Reserved}    可用：{result.Balance - result.Reserved} 积分";
        string confirmation = result.AlreadyRedeemed ? "此兑换码已入账到当前账户，本次未重复增加积分。" : $"兑换成功，已增加 {result.Credits} 积分。";
        try { await RefreshBalanceAndLedger(service, session); }
        catch (Exception) when (!closing && !lifetime.IsCancellationRequested)
        {
            if (IsCurrentSession(service, session)) status.Text = confirmation + " 列表刷新未完成，请稍后刷新。";
            return;
        }
        if (IsCurrentSession(service, session)) status.Text = confirmation;
    }

    private async Task Purchase()
    {
        if (plans.SelectedItem is not CreditPlan plan) throw new InvalidOperationException("请先登录并刷新次数包列表。");
        if (pendingOrderRequest is null || pendingPlan != plan.Id) { pendingOrderRequest = Guid.NewGuid(); pendingPlan = plan.Id; }
        var order = await EnsureClient().CreateOrderAsync(plan.Id, pendingOrderRequest.Value, lifetime.Token);
        if (closing) return;
        // Keep this request ID until a confirmed response, so retrying a disconnected create cannot duplicate an order.
        pendingOrderRequest = null; pendingPlan = null;
        UpsertOrder(order);
        if (order.Status is "paid" or "completed") { status.Text = "订单已支付，请刷新余额。"; return; }
        OpenPayment(order);
        status.Text = "已打开订单支付页。付款后请刷新所选订单。";
    }

    private Task OpenSelectedPayment() { OpenPayment(SelectedOrder()); return Task.CompletedTask; }
    private void OpenPayment(CreditOrder order)
    {
        if (string.IsNullOrWhiteSpace(order.PayUrl)) throw new InvalidOperationException("此订单没有可打开的支付地址，请刷新订单。");
        var uri = CreditServiceClient.ValidatePaymentUrl(order.PayUrl, ServerUrl);
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    private async Task RefreshSelectedOrder()
    {
        var selected = SelectedOrder();
        var service = EnsureClient(); var session = service.Session;
        var updated = await service.GetOrderAsync(selected.Id, refresh: true, lifetime.Token);
        if (!IsCurrentSession(service, session)) return;
        UpsertOrder(updated);
        await RefreshBalanceAndLedger(service, session);
        if (IsCurrentSession(service, session)) status.Text = "订单状态：" + updated.Status + "。余额已刷新。";
    }

    private CreditOrder SelectedOrder() => orders.SelectedItems.Count > 0 && orders.SelectedItems[0].Tag is CreditOrder order
        ? order : throw new InvalidOperationException("请先选择一个订单。");
    private void PopulateOrders(IEnumerable<CreditOrder> rows)
    {
        string? selected = orders.SelectedItems.Count > 0 ? (orders.SelectedItems[0].Tag as CreditOrder)?.Id : null;
        orders.BeginUpdate();
        try
        {
            orders.Items.Clear();
            foreach (var order in rows.Take(1000)) { var item = OrderItem(order); item.Selected = order.Id == selected; orders.Items.Add(item); }
        }
        finally { orders.EndUpdate(); }
    }
    private void UpsertOrder(CreditOrder order)
    {
        var existing = orders.Items.Cast<ListViewItem>().FirstOrDefault(i => (i.Tag as CreditOrder)?.Id == order.Id);
        if (existing is not null) orders.Items.Remove(existing);
        var item = OrderItem(order); orders.Items.Insert(0, item); item.Selected = true; item.EnsureVisible();
    }
    private static ListViewItem OrderItem(CreditOrder order) => new([
        order.CreatedAt.ToLocalTime().ToString("MM-dd HH:mm:ss"), order.Status, order.Credits.ToString(),
        "¥" + (order.AmountFen / 100m).ToString("0.00"), order.Id]) { Tag = order };

    private async Task Recover()
    {
        var result = await EnsureClient().ResumeLatestAsync(lifetime.Token);
        if (!closing) ShowRecent(result);
    }
    private async Task CancelRecent()
    {
        var service = EnsureClient();
        if (service.Session?.RecentTaskId is not Guid id) throw new InvalidOperationException("没有可取消的最近任务。");
        var result = await service.CancelAsync(id, lifetime.Token);
        if (!closing) { ShowRecent(result); status.Text = "已查询取消结果。已完成任务的费用不会回滚。"; }
    }
    private void ShowRecent(CreditTranslationResult result)
    {
        recent.Text = $"任务：{result.Id}\r\n状态：{(result.IsExpired ? "译文已过期" : result.Status)}    总余额：{result.Balance}\r\n\r\n原文\r\n{result.SourceText}\r\n\r\n译文\r\n{result.Translation}\r\n{result.Error}";
        status.Text = result.IsExpired ? result.Error : result.IsTerminal ? "最近任务已恢复，仅显示在本窗口。" : "任务仍在处理中，可稍后再次查询。";
    }
    private void ClearDisplays() { balance.Text = "尚未刷新账户"; plans.Items.Clear(); orders.Items.Clear(); ledger.Items.Clear(); recent.Clear(); redemptionCode.Clear(); }

    private Button ActionButton(string label, Func<Task> action, bool primary = false)
    {
        var button = new UiButton { Text = label, AutoSize = true, Primary = primary, MinimumSize = new Size(100, 38), Margin = new Padding(0, 4, 10, 4) };
        actionButtons.Add(button);
        button.Click += async (_, _) =>
        {
            if (working || closing) return;
            working = true; SetBusy(true); status.Text = "正在处理…";
            try { await action(); }
            catch (OperationCanceledException) { if (!closing) status.Text = "操作已取消。"; }
            catch (Exception error) { if (!closing) status.Text = error is InvalidOperationException ? error.Message : "操作未完成，请稍后重试。"; }
            finally { working = false; if (!closing) SetBusy(false); }
        };
        return button;
    }
    private void SetBusy(bool value)
    {
        foreach (var button in actionButtons) button.Enabled = !value;
        endpoint.Enabled = username.Enabled = password.Enabled = email.Enabled = verificationCode.Enabled = redemptionCode.Enabled = plans.Enabled = !value;
        UpdateRegistrationControls();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !resourcesDisposed)
        {
            resourcesDisposed = true; closing = true;
            resendTimer.Dispose();
            var pendingCancellation = CancelLifetimeAsync();
            var ownedClient = client; client = null;
            _ = DisposeAfterCancellation(pendingCancellation, ownedClient, lifetime);
        }
        base.Dispose(disposing);
    }
    private Task CancelLifetimeAsync() => lifetimeCancellation ??= BackgroundCancellation.RequestAsync(lifetime);
    private static async Task DisposeAfterCancellation(Task cancellation, CreditServiceClient? client, CancellationTokenSource source)
    {
        await cancellation.ConfigureAwait(false);
        try { client?.Dispose(); }
        catch (Exception error) { RuntimeFaultLog.Record("credit-client-dispose", error); }
        finally { source.Dispose(); }
    }
    private static TextBox Input(string name) => new() { Name = name, Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, BackColor = UiTheme.Input, ForeColor = UiTheme.Ink };
    private static Label Note(string text) => new() { Text = text, AutoSize = true, ForeColor = UiTheme.Muted, UseMnemonic = false, MaximumSize = new Size(860, 0), Margin = new Padding(0, 6, 0, 10) };
    private static Panel Frame(TextBox field) { var panel = new Panel { Height = 42, Dock = DockStyle.Top, BackColor = UiTheme.Input, Padding = new Padding(12, 10, 12, 8), Margin = new Padding(0, 0, 0, 10) }; panel.Controls.Add(field); return panel; }
    private static FlowLayoutPanel Flow() => new() { AutoSize = true, Dock = DockStyle.Top, WrapContents = true, Margin = Padding.Empty };
    private static TableLayoutPanel Stack() { var panel = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1 }; panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); return panel; }
    private static void Add(TableLayoutPanel stack, Control value) { int row = stack.RowCount++; stack.RowStyles.Add(new RowStyle(SizeType.AutoSize)); value.Dock = DockStyle.Top; stack.Controls.Add(value, 0, row); }
    private static TabPage Page(TabControl tabs, string title) { var page = new TabPage(title) { BackColor = UiTheme.Card, ForeColor = UiTheme.Ink, Padding = new Padding(18), AutoScroll = true }; tabs.TabPages.Add(page); return page; }
    private static ListView Table(string name, params (string Title, int Width)[] columns)
    {
        var table = new ListView { Name = name, Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false,
            HideSelection = false, BackColor = UiTheme.Input, ForeColor = UiTheme.Ink, BorderStyle = BorderStyle.None };
        foreach (var column in columns) table.Columns.Add(column.Title, column.Width);
        return table;
    }
}
