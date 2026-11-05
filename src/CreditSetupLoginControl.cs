using System.Net;

namespace SteamVrTranslator;

internal sealed class CreditSetupLoginControl : UserControl
{
    private readonly Func<string, string, string, CancellationToken, Task<CreditSession>> authenticate;
    private readonly TextBox username = Field("wizardLoginUsername", "账号", 32);
    private readonly TextBox password = Field("wizardLoginPassword", "密码", 128);
    private readonly UiButton submit = new() { Name = "wizardLoginSubmit", Text = "登录", Primary = true,
        Size = new Size(120, 40), Margin = new Padding(0, 0, 20, 0), TabIndex = 0 };
    private readonly LinkLabel register = new() { Name = "wizardLoginRegister", Text = "注册新账号",
        AutoSize = true, LinkColor = UiTheme.Muted, ActiveLinkColor = UiTheme.Accent, VisitedLinkColor = UiTheme.Muted,
        Anchor = AnchorStyles.Left, Margin = Padding.Empty, TabIndex = 1 };
    private readonly Label status = new() { Name = "wizardLoginStatus", AutoSize = true, Visible = false,
        ForeColor = UiTheme.Muted, Margin = new Padding(0, 14, 0, 0) };
    private string serverUrl;
    private string? signedInUsername;
    private DateTimeOffset sessionExpires;
    private CancellationTokenSource? pending;
    private bool registrationAvailable = true;

    internal bool IsBusy => pending is not null;
    internal bool IsAuthenticated => signedInUsername is not null && sessionExpires > DateTimeOffset.UtcNow;
    internal string? AuthenticatedUsername => IsAuthenticated ? signedInUsername : null;
    internal Button LoginButton => submit;
    [System.ComponentModel.DefaultValue(true)]
    internal bool RegistrationAvailable
    {
        get => registrationAvailable;
        set { registrationAvailable = value; register.Visible = value; }
    }
    internal event EventHandler? SignedIn;
    internal event EventHandler? StateChanged;
    internal event EventHandler? RegisterRequested;

    internal CreditSetupLoginControl(string serverUrl,
        Func<string, string, string, CancellationToken, Task<CreditSession>>? authenticate = null)
    {
        this.serverUrl = CreditServiceClient.NormalizeServerUrl(serverUrl);
        this.authenticate = authenticate ?? LoginAsync;
        Name = "wizardCreditLogin";
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = UiTheme.Background;
        ForeColor = UiTheme.Ink;
        Margin = Padding.Empty;
        password.UseSystemPasswordChar = true;
        var fields = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1, Dock = DockStyle.Top, Margin = Padding.Empty, Padding = new Padding(2, 2, 2, 8) };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddField(fields, username, 0);
        AddField(fields, password, 1);
        var actions = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = Padding.Empty, WrapContents = true, TabIndex = 2 };
        actions.Controls.Add(submit);
        actions.Controls.Add(register);
        fields.Controls.Add(actions, 0, 4);
        fields.Controls.Add(status, 0, 5);
        Controls.Add(fields);
        submit.Click += async (_, _) => await TryLoginAsync();
        register.LinkClicked += (_, _) => { if (!IsBusy && RegistrationAvailable) RegisterRequested?.Invoke(this, EventArgs.Empty); };
    }

    private static TextBox Field(string name, string label, int length) => new()
    {
        Name = name, AccessibleName = label, BackColor = UiTheme.Input, ForeColor = UiTheme.Ink,
        BorderStyle = BorderStyle.None, MaxLength = length, Dock = DockStyle.Top, Margin = Padding.Empty
    };

    private static void AddField(TableLayoutPanel fields, TextBox input, int index)
    {
        var label = new Label { Text = input.AccessibleName, AutoSize = true,
            ForeColor = UiTheme.Muted, Margin = new Padding(0, 0, 0, 6) };
        var frame = new UiCard { HeaderHeight = 0, Height = 42, BackColor = UiTheme.Input, Dock = DockStyle.Top,
            MinimumSize = new Size(0, 42), MaximumSize = new Size(440, 42), Padding = new Padding(12, 10, 12, 8),
            Margin = new Padding(0, 0, 0, 16), TabIndex = index };
        input.TabIndex = index;
        frame.Controls.Add(input);
        frame.Click += (_, _) => input.Focus();
        fields.Controls.Add(label, 0, index * 2);
        fields.Controls.Add(frame, 0, index * 2 + 1);
    }

    internal void FocusUsername() => username.Focus();

    internal void SetServer(string url)
    {
        string next = CreditServiceClient.NormalizeServerUrl(url);
        if (next == serverUrl) return;
        serverUrl = next;
        signedInUsername = null;
        username.Clear();
        CancelPending();
    }

    internal void CancelPending()
    {
        var previous = pending;
        pending = null;
        previous?.Cancel();
        password.Clear();
        SetStatus("");
        UpdateEnabled();
    }

    private static async Task<CreditSession> LoginAsync(string url, string user, string secret, CancellationToken token)
    {
        using var client = new CreditServiceClient(url);
        return await client.LoginAsync(user, secret, token).ConfigureAwait(false);
    }

    internal async Task<bool> TryLoginAsync()
    {
        if (IsBusy || IsDisposed || Disposing) return false;
        string user;
        try { user = CreditServiceClient.ValidateCredentials(username.Text, password.Text); }
        catch (InvalidOperationException error) { SetStatus(error.Message); return false; }

        using var attempt = new CancellationTokenSource();
        pending = attempt;
        signedInUsername = null;
        SetStatus("正在登录…");
        UpdateEnabled();
        try
        {
            var session = await authenticate(serverUrl, user, password.Text, attempt.Token);
            if (pending != attempt) return false;
            if (session is null || session.IsExpired || !string.Equals(session.Username, user, StringComparison.OrdinalIgnoreCase))
            {
                SetStatus("服务返回的登录信息无效，请重试。");
                return false;
            }
            signedInUsername = session.Username;
            sessionExpires = session.ExpiresAt;
            password.Clear();
            SetStatus("已登录");
        }
        catch (Exception error)
        {
            if (pending != attempt) return false;
            SetStatus(error switch
            {
                CreditServiceException { StatusCode: HttpStatusCode.Unauthorized } => "账号或密码错误，请重新输入。",
                CreditServiceException { StatusCode: HttpStatusCode.TooManyRequests } => "登录次数过多，请稍后重试。",
                CreditServiceException { StatusCode: not null, IsTransient: true } => "积分服务暂时不可用，请稍后重试。",
                CreditServiceException { IsTransient: true } or HttpRequestException or OperationCanceledException =>
                    "暂时无法连接积分服务，请检查网络后重试。",
                IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException =>
                    "无法保存本机登录信息，请检查当前用户的文件权限后重试。",
                _ => "登录未完成，请重试。"
            });
            return false;
        }
        finally
        {
            if (pending == attempt) { pending = null; UpdateEnabled(); }
        }
        SignedIn?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void SetStatus(string text)
    {
        status.Text = text;
        status.Visible = text.Length > 0;
    }

    private void UpdateEnabled()
    {
        username.Enabled = password.Enabled = submit.Enabled = register.Enabled = !IsBusy;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && pending is { } attempt)
        {
            pending = null;
            attempt.Cancel();
        }
        base.Dispose(disposing);
    }
}
