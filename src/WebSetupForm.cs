using System.Net;
using System.Text.Json;

namespace SteamVrTranslator;

internal sealed class WebSetupForm : Form
{
    private readonly WebSetupFlow flow;

    internal WebSetupForm(AppSettings initial, Action<AppSettings> save, bool firstRun = true, bool startAtLogin = false,
        string? profileDirectory = null, Func<string, string, string, CancellationToken, Task<CreditSession>>? authenticate = null,
        bool loginOnly = false)
    {
        Text = loginOnly ? "无言势翻译器 · 账号登录" : firstRun ? "无言势翻译器 · 初次设置" : "无言势翻译器 · 重新配置";
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Color.FromArgb(16, 23, 27);
        MinimumSize = new Size(440, 430);
        Size = new Size(680, 680);
        MinimizeBox = false;
        ShowInTaskbar = firstRun;
        StartPosition = firstRun ? FormStartPosition.CenterScreen : FormStartPosition.CenterParent;
        var web = new LocalWebSurface("setup.html", ReceiveAsync, profileDirectory) { Dock = DockStyle.Fill };
        flow = new WebSetupFlow(initial, save, web.Post, result =>
        {
            DialogResult = result;
            Close();
        }, firstRun, startAtLogin, authenticate, loginOnly);
        Controls.Add(web);
        WindowChrome.Apply(this);
    }

    private Task ReceiveAsync(JsonElement message) => flow.ReceiveAsync(message);

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        var area = (Owner is null ? Screen.FromControl(this) : Screen.FromControl(Owner)).WorkingArea;
        MinimumSize = new Size(Math.Min(MinimumSize.Width, area.Width), Math.Min(MinimumSize.Height, area.Height));
        Size = new Size(Math.Min(Width, area.Width), Math.Min(Height, area.Height));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) flow?.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>The trusted host owns navigation, service destinations, credentials and persistence.
/// The local page receives display fields and whether a reusable key exists, never the key itself.</summary>
internal sealed class WebSetupFlow : IDisposable
{
    private readonly AppSettings baseline;
    private AppSettings draft;
    private readonly Action<AppSettings> save;
    private readonly Action<object> post;
    private readonly Action<DialogResult> close;
    private readonly Func<string, string, string, CancellationToken, Task<CreditSession>> authenticate;
    private readonly Func<string,CreditServiceClient> registrationService;
    private const string Official = SetupWizardForm.OfficialCreditServerUrl;
    private readonly string? endpoint;
    private readonly bool firstRun;
    private readonly bool loginOnly;
    private CancellationTokenSource? attempt;
    private DateTimeOffset signedInUntil;
    private bool finished;
    private bool registering;
    private string? codeAddress;
    private string? cooldownEmail;
    private DateTimeOffset resendAfter;
    private int step;

    internal WebSetupFlow(AppSettings initial, Action<AppSettings> save, Action<object> post, Action<DialogResult> close,
        bool firstRun, bool startAtLogin, Func<string, string, string, CancellationToken, Task<CreditSession>>? authenticate = null,
        bool loginOnly = false, Func<string,CreditServiceClient>? registrationService = null)
    {
        this.save = save;
        this.post = post;
        this.close = close;
        this.firstRun = firstRun;
        this.authenticate = authenticate ?? LoginAsync;
        this.registrationService = registrationService ?? (url => new CreditServiceClient(url));
        this.loginOnly = loginOnly;
        baseline = initial.Copy();
        if (firstRun && !loginOnly)
        {
            baseline.TranslationBackend = "Credits";
            baseline.CreditServerUrl = Official;
            baseline.ProcessingMode = "Handwriting";
            baseline.RuntimeMode = "Auto";
            baseline.DesktopMode = false;
        }
        try { endpoint = CreditServiceClient.NormalizeServerUrl(baseline.CreditServerUrl); }
        catch (InvalidOperationException) { endpoint = null; }
        draft = baseline.Copy();
        if (loginOnly) draft.TranslationBackend = "Credits";
        step = loginOnly || (startAtLogin && draft.TranslationBackend == "Credits") ? 1 : 0;
    }

    private bool SignedIn => signedInUntil > DateTimeOffset.UtcNow;

    internal async Task ReceiveAsync(JsonElement message)
    {
        if (finished || message.ValueKind != JsonValueKind.Object) return;
        try
        {
            switch (TextValue(message, "type"))
            {
                case "ready":
                    if (loginOnly)
                    {
                        post(new { type = "init", loginOnly, canLogin = endpoint != null, loginOrigin = endpoint == Official + "/" ? "" : endpoint });
                        ShowStep();
                        break;
                    }
                    post(new {
                        type = "init", firstRun, loginOnly, backend = draft.TranslationBackend, draft.RuntimeMode,
                        draft.CaptureMode, draft.Hotkey, draft.ControllerEnabled, draft.ProcessingMode, draft.TargetLanguage,
                        draft.CodexExecutablePath, draft.CodexModel, draft.CodexReasoningEffort, draft.ApiBaseUrl, draft.ApiModel,
                        draft.ApiStream, draft.ApiImageDetail, hasApiKey = draft.ApiKey.Length > 0, presets = ApiProviderPresets.All,
                        canLogin = endpoint != null, loginOrigin = endpoint == Official + "/" ? "" : endpoint
                    });
                    ShowStep();
                    break;
                case "service" when step == 0:
                    var backend = Choice(message, "backend", "Credits", "Codex", "OpenAI", "GoogleTranslate");
                    if (draft.TranslationBackend != backend)
                    {
                        draft = baseline.Copy();
                        draft.TranslationBackend = backend;
                    }
                    step = backend == "Credits" && SignedIn ? 2 : 1;
                    ShowStep();
                    break;
                case "login" when step == 1 && draft.TranslationBackend == "Credits" && attempt == null && !registering:
                case "signup" when step == 1 && draft.TranslationBackend == "Credits" && attempt == null && registering:
                    await SignInAsync(message);
                    break;
                case "register" when step == 1 && draft.TranslationBackend == "Credits" && attempt == null && endpoint != null:
                    registering = true;
                    ShowStep();
                    break;
                case "sendCode" when step == 1 && registering && attempt == null:
                    await RequestCodeAsync(TextValue(message, "email").Trim());
                    break;
                case "configure" when step == 1 && draft.TranslationBackend != "Credits":
                    ConfigureProvider(message);
                    step = 2;
                    ShowStep();
                    break;
                case "finish" when step == 2:
                    Finish(message);
                    break;
                case "back" when step > 0:
                    CancelPending();
                    if (registering)
                    {
                        registering = false;
                        codeAddress = null;
                        ShowStep();
                        break;
                    }
                    if (loginOnly) { Dispose(); close(DialogResult.Cancel); break; }
                    step = step == 2 && draft.TranslationBackend != "Credits" ? 1 : 0;
                    ShowStep();
                    break;
                case "cancel":
                    Dispose();
                    close(DialogResult.Cancel);
                    break;
            }
        }
        catch (Exception error) when (!finished)
        {
            // Only local validation messages may be displayed. Never reflect arbitrary
            // exception details, upstream response bodies, passwords, tokens, or API keys.
            string text = error is InvalidOperationException or ArgumentException ? error.Message :
                error is IOException or UnauthorizedAccessException ? "无法保存本机信息，请检查文件权限后重试。" : "操作未完成，请重试。";
            ShowStep(text);
        }
    }

    private void ConfigureProvider(JsonElement message)
    {
        var next = draft.Copy();
        next.TargetLanguage = TextValue(message, "targetLanguage").Trim();
        next.ProcessingMode = Choice(message, "processingMode", "Ocr", "Image", "Handwriting", "LocalJapanese");
        if (next.TranslationBackend == "Codex")
        {
            next.CodexExecutablePath = TextValue(message, "codexPath").Trim();
            next.CodexModel = TextValue(message, "codexModel").Trim();
            next.CodexReasoningEffort = Choice(message, "reasoning", "", "low", "medium", "high");
        }
        else if (next.TranslationBackend == "OpenAI")
        {
            string address = TextValue(message, "apiBaseUrl").Trim();
            string entered = TextValue(message, "apiKey").Trim();
            if (entered.Length > 8192 || entered.Any(char.IsControl))
                throw new InvalidOperationException("API 密钥无效，请重新填写。");
            if (entered.Length > 0 || ApiProviderPresets.ChangesAuthority(next.ApiBaseUrl, address) || Flag(message, "clearKey"))
                next.ApiKey = entered;
            next.ApiBaseUrl = address;
            next.ApiModel = TextValue(message, "apiModel").Trim();
            next.ApiStream = Flag(message, "apiStream");
            next.ApiImageDetail = Choice(message, "apiImageDetail", "Default", "High", "Low", "Auto");
        }
        if (next.TargetLanguage.Length is 0 or > 100 || next.TargetLanguage.Any(char.IsControl))
            throw new InvalidOperationException("请填写有效的目标语言。");
        SettingsValidation.ValidateBackendMode(next);
        if (next.TranslationBackend == "OpenAI") TranslationClient.ValidateSettings(next);
        if (next.TranslationBackend == "GoogleTranslate") GoogleTranslateClient.NormalizeTargetLanguage(next.TargetLanguage);
        if (next.TranslationBackend == "Codex" && (next.CodexModel.Length > 256 || next.CodexModel.Any(char.IsControl) ||
            next.CodexExecutablePath.Length > 4096 || next.CodexExecutablePath.Any(char.IsControl)))
            throw new InvalidOperationException("请检查 Codex 的程序路径和模型名称。");
        draft = next;
    }

    private void Finish(JsonElement message)
    {
        if (draft.TranslationBackend == "Credits" && !SignedIn)
        {
            step = 1;
            ShowStep("请先登录账号。");
            return;
        }
        var final = draft.Copy();
        final.RuntimeMode = Choice(message, "runtimeMode", "Auto", "Desktop", "SteamVR");
        final.DesktopMode = final.RuntimeMode == "Desktop";
        final.Hotkey = TextValue(message, "hotkey").Trim();
        if (!final.DesktopMode)
        {
            final.ControllerEnabled = Flag(message, "controller");
            final.CaptureMode = Choice(message, "captureMode", "VR 左眼", "桌面主显示器");
        }
        SettingsValidation.ValidateForSave(final, requireServiceConfiguration: true);
        save(final);
        finished = true;
        close(DialogResult.OK);
    }

    private async Task SignInAsync(JsonElement message)
    {
        if (endpoint is null) { ShowStep("服务地址无效，请先返回并重新选择服务。"); return; }
        string password = TextValue(message, "password");
        string user = CreditServiceClient.ValidateCredentials(TextValue(message, "username"), password);
        string email = registering ? TextValue(message, "email").Trim() : "";
        string code = registering ? TextValue(message, "code").Trim() : "";
        if (registering && (!string.Equals(email, codeAddress, StringComparison.OrdinalIgnoreCase) ||
            code.Length != 6 || !code.All(char.IsAsciiDigit)))
        {
            ShowStep("请为此邮箱获取验证码，并填写收到的 6 位数字。");
            return;
        }
        using var request = new CancellationTokenSource();
        attempt = request;
        ShowStep();
        string errorText = "";
        try
        {
            CreditSession session;
            if (registering)
            {
                using var service = registrationService(endpoint);
                session = await service.RegisterAsync(user, password, email, code, request.Token);
            }
            else session = await authenticate(endpoint, user, password, request.Token);
            if (attempt != request || finished) return;
            if (!session.IsExpired && string.Equals(user, session.Username, StringComparison.OrdinalIgnoreCase))
            {
                signedInUntil = session.ExpiresAt;
                registering = false;
                codeAddress = null;
                step = 2;
            }
            else errorText = "登录信息无效，请重试。";
        }
        catch (Exception error)
        {
            if (attempt != request || finished) return;
            errorText = error switch
            {
                CreditServiceException safe when registering => safe.Message,
                CreditServiceException { StatusCode: HttpStatusCode.Unauthorized } => "账号或密码错误，请重新输入。",
                CreditServiceException { StatusCode: HttpStatusCode.TooManyRequests } => "登录次数过多，请稍后重试。",
                CreditServiceException { IsTransient: true } or HttpRequestException or OperationCanceledException => "暂时无法连接积分服务，请检查网络后重试。",
                IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException => "无法保存本机登录信息，请检查文件权限后重试。",
                _ => "登录未完成，请重试。"
            };
        }
        finally
        {
            if (attempt == request) attempt = null;
        }
        if (loginOnly && SignedIn)
        {
            finished = true;
            close(DialogResult.OK);
        }
        else if (!finished) ShowStep(errorText);
    }

    private async Task RequestCodeAsync(string email)
    {
        if (endpoint is null) return;
        if (email.Equals(cooldownEmail, StringComparison.OrdinalIgnoreCase) && resendAfter > DateTimeOffset.UtcNow)
        {
            ShowStep("请稍候再获取验证码。"); return;
        }
        using var request = new CancellationTokenSource();
        attempt = request;
        codeAddress = null;
        ShowStep();
        string errorText = "", notice = "";
        try
        {
            using var service = registrationService(endpoint);
            await service.RequestRegistrationCodeAsync(email, request.Token);
            if (attempt != request || finished) return;
            codeAddress = email;
            cooldownEmail = email;
            resendAfter = DateTimeOffset.UtcNow.AddSeconds(60);
            notice = "请查看邮箱并填写最新的验证码，10 分钟内有效。";
        }
        catch (Exception error)
        {
            if (attempt != request || finished) return;
            if (error is CreditServiceException { StatusCode: HttpStatusCode.TooManyRequests })
            {
                cooldownEmail = email; resendAfter = DateTimeOffset.UtcNow.AddSeconds(60);
            }
            errorText = error is CreditServiceException or InvalidOperationException ? error.Message : "发信未完成，请稍后重试。";
        }
        finally { if (attempt == request) attempt = null; }
        if (!finished) ShowStep(errorText, notice);
    }

    private static async Task<CreditSession> LoginAsync(string url, string user, string secret, CancellationToken token)
    {
        using var client = new CreditServiceClient(url);
        return await client.LoginAsync(user, secret, token);
    }

    private void ShowStep(string error = "", string notice = "") => post(new { type = "step", step, backend = draft.TranslationBackend,
        busy = attempt != null, registering, codeAddress, cooldownEmail, resendAfterMs = resendAfter.ToUnixTimeMilliseconds(),
        error, notice, draft.ProcessingMode, hasApiKey = !loginOnly && draft.ApiKey.Length > 0, keyOrigin = loginOnly ? "" : draft.ApiBaseUrl });

    private static string TextValue(JsonElement json, string name) =>
        json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { } text
            ? text : throw new InvalidOperationException("此项配置无效，请检查后重试。");

    private static string Choice(JsonElement json, string name, params string[] allowed)
    {
        string value = TextValue(json, name);
        if (!allowed.Contains(value, StringComparer.Ordinal)) throw new InvalidOperationException("请选择有效的选项。");
        return value;
    }

    private static bool Flag(JsonElement json, string name) =>
        json.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean() : throw new InvalidOperationException("此项设置无效，请重试。");

    private void CancelPending()
    {
        var previous = attempt;
        attempt = null;
        previous?.Cancel();
    }

    public void Dispose() { finished = true; CancelPending(); }
}
