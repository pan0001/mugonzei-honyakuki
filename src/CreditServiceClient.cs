using System.Net;
using System.Net.Http.Headers;
using System.Net.Mail;
using System.Text;
using System.Text.Json;

namespace SteamVrTranslator;

public sealed class CreditServiceClient : IDisposable
{
    public const int MaxTextCharacters = 16_000;
    public const int MaxImageBytes = 12 * 1024 * 1024;
    private const int MaxResponseBytes = 1_048_576;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient http;
    private readonly CreditSessionStore sessions;
    private readonly TimeSpan pollInterval;
    public string ServerUrl { get; }
    public CreditSession? Session => sessions.Load(ServerUrl);

    public CreditServiceClient(string serverUrl) : this(serverUrl, new CreditSessionStore()) { }
    public CreditServiceClient(string serverUrl, CreditSessionStore sessions)
        : this(serverUrl, sessions, new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }, TimeSpan.FromSeconds(1)) { }
    internal CreditServiceClient(string serverUrl, CreditSessionStore sessions, HttpMessageHandler handler, TimeSpan? pollInterval = null)
    {
        ServerUrl = NormalizeServerUrl(serverUrl);
        this.sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        this.pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(1);
        http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public static string NormalizeServerUrl(string serverUrl)
    {
        if (string.IsNullOrWhiteSpace(serverUrl) || serverUrl.Length > 2048 || serverUrl.Any(char.IsControl)
            || serverUrl.Contains('\\') || !Uri.TryCreate(serverUrl.Trim(), UriKind.Absolute, out var uri)
            || string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
            || (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
            throw new InvalidOperationException("积分服务地址须为 HTTPS；仅本机允许 HTTP，且不能包含账号、密码、查询参数或片段。");
        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/";
    }

    public static Uri ValidatePaymentUrl(string payUrl, string serverUrl)
    {
        var server = new Uri(NormalizeServerUrl(serverUrl));
        if (string.IsNullOrWhiteSpace(payUrl) || payUrl.Length > 4096 || payUrl.Any(char.IsControl)
            || payUrl.Contains('\\') || !Uri.TryCreate(payUrl, UriKind.Absolute, out var payment)
            || payment.UserInfo.Length != 0 || payment.Fragment.Length != 0)
            throw new InvalidOperationException("支付地址无效，未打开浏览器。");
        bool local = server.IsLoopback && payment.IsLoopback && server.Scheme == payment.Scheme
            && server.IdnHost == payment.IdnHost && server.Port == payment.Port;
        bool official = payment.Scheme == Uri.UriSchemeHttps && payment.IsDefaultPort
            && payment.IdnHost is "afdian.com" or "www.afdian.com" or "afdian.net" or "www.afdian.net" or "ifdian.net" or "www.ifdian.net";
        if (!local && !official) throw new InvalidOperationException("支付地址不属于支持的爱发电域名或当前本机测试服务，未打开浏览器。");
        return payment;
    }

    public async Task RequestRegistrationCodeAsync(string email, CancellationToken token = default)
    {
        email = ValidateEmail(email);
        try
        {
            await SendAsync<JsonElement>(HttpMethod.Post, "v1/auth/register/email-code", new { email }, null,
                token, allowEmpty: true, expectedStatus: HttpStatusCode.NoContent).ConfigureAwait(false);
        }
        catch (CreditServiceException error) when (error.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
        { throw new CreditServiceException("此积分服务尚未支持邮箱验证码，请更新服务端或联系运营者。", error.StatusCode); }
        catch (CreditServiceException error) when (error.StatusCode == HttpStatusCode.ServiceUnavailable)
        { throw new CreditServiceException("暂时无法发送验证码，请稍后重试或联系运营者。已有账号可直接登录。", error.StatusCode); }
        catch (CreditServiceException error) when (error.StatusCode == HttpStatusCode.TooManyRequests)
        { throw new CreditServiceException("验证码发送次数已达限制，请稍后重试。", error.StatusCode); }
        catch (CreditServiceException error) when (error.StatusCode is null && error.IsTransient)
        { throw new CreditServiceException("发送结果尚未确认。请先查看收件箱和垃圾邮件，再决定是否重试。", isTransient: true); }
    }

    public async Task<CreditSession> RegisterAsync(string username, string password, string email, string code, CancellationToken token = default)
    {
        username = ValidateCredentials(username, password);
        email = ValidateEmail(email);
        if (code is not { Length: 6 } || !code.All(char.IsAsciiDigit))
            throw new InvalidOperationException("请输入邮件中的 6 位数字验证码。");
        try
        {
            return await AuthenticateAsync("register", username, new { username, password, email, code }, token).ConfigureAwait(false);
        }
        catch (CreditServiceException error) when (error.StatusCode == HttpStatusCode.BadRequest)
        { throw new CreditServiceException("注册信息或验证码无效，请检查邮箱和最新的 6 位验证码。", error.StatusCode); }
        catch (CreditServiceException error) when (error.StatusCode == HttpStatusCode.Conflict)
        { throw new CreditServiceException("用户名或邮箱无法用于新账户。已有账号可返回登录。", error.StatusCode); }
        catch (CreditServiceException error) when (error.StatusCode == HttpStatusCode.ServiceUnavailable)
        { throw new CreditServiceException("邮箱验证服务暂时不可用，请稍后重试或联系运营者。", error.StatusCode); }
        catch (CreditServiceException error) when (error.StatusCode is null && error.IsTransient)
        { throw new CreditServiceException("注册结果尚未确认。请先尝试用刚填写的用户名和密码登录。", isTransient: true); }
    }

    public Task<CreditSession> LoginAsync(string username, string password, CancellationToken token = default)
    {
        username = ValidateCredentials(username, password);
        return AuthenticateAsync("login", username, new { username, password }, token);
    }

    private static string ValidateEmail(string email)
    {
        string value = (email ?? "").Trim();
        if (value.Length is 0 or > 254 || value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))
            || !MailAddress.TryCreate(value, out var parsed) || parsed.Address != value || !parsed.Host.Contains('.'))
            throw new InvalidOperationException("请填写有效的邮箱地址，例如 name@example.com。");
        return value;
    }

    internal static string ValidateCredentials(string username, string password)
    {
        username = (username ?? "").Trim().ToLowerInvariant();
        if (username.Length is < 3 or > 32 || username.Any(c => !(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-')))
            throw new InvalidOperationException("用户名需为 3–32 位英文字母、数字、下划线或连字符。");
        if (string.IsNullOrEmpty(password) || password.Length is < 10 or > 128 || password.Any(char.IsControl))
            throw new InvalidOperationException("密码需为 10–128 个字符，且不能包含换行或控制字符。");
        return username;
    }

    private async Task<CreditSession> AuthenticateAsync(string action, string username, object credentials, CancellationToken token)
    {
        var response = await SendAsync<LoginResponse>(HttpMethod.Post, "v1/auth/" + action, credentials, null, token).ConfigureAwait(false);
        if (response.ExpiresAt <= DateTimeOffset.UtcNow || !string.Equals(username, response.Username, StringComparison.OrdinalIgnoreCase))
            throw InvalidResponse();
        return sessions.Save(ServerUrl, response.Token, response.Username, response.ExpiresAt, token);
    }

    public async Task LogoutAsync(CancellationToken token = default)
    {
        var session = sessions.Load(ServerUrl);
        if (session is null) return;
        try { await SendAsync<JsonElement>(HttpMethod.Post, "v1/auth/logout", null, session, token, allowEmpty: true).ConfigureAwait(false); }
        finally { sessions.Remove(ServerUrl, session.Token); }
    }

    public void ClearLocalSession() => sessions.Remove(ServerUrl);
    internal void ClearLocalSession(string expectedToken) => sessions.Remove(ServerUrl, expectedToken);
    public Task<CreditAccount> GetAccountAsync(CancellationToken token = default) => SendAsync<CreditAccount>(HttpMethod.Get, "v1/account", null, RequireSession(), token);
    public Task<CreditLedgerEntry[]> GetLedgerAsync(CancellationToken token = default) => SendAsync<CreditLedgerEntry[]>(HttpMethod.Get, "v1/ledger", null, RequireSession(), token);
    public Task<CreditPlan[]> GetPlansAsync(CancellationToken token = default) => SendAsync<CreditPlan[]>(HttpMethod.Get, "v1/plans", null, RequireSession(), token);
    public Task<CreditOrder[]> GetOrdersAsync(CancellationToken token = default) => SendAsync<CreditOrder[]>(HttpMethod.Get, "v1/orders", null, RequireSession(), token);

    public async Task<CreditRedemptionResult> RedeemAsync(string code, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(code) || code.Length > 256)
            throw new InvalidOperationException("请输入有效兑换码，格式为 MU 后接 8 组四位字母或数字。");
        string compact = string.Concat(code.Where(c => !char.IsWhiteSpace(c) && c != '-')).ToUpperInvariant();
        if (compact.Length != 34 || !compact.StartsWith("MU", StringComparison.Ordinal)
            || compact.AsSpan(2).ContainsAnyExcept("0123456789ABCDEF"))
            throw new InvalidOperationException("请输入有效兑换码，格式为 MU 后接 8 组四位十六进制字符。");
        string normalized = "MU-" + string.Join("-", Enumerable.Range(0, 8).Select(i => compact.Substring(2 + i * 4, 4)));
        CreditRedemptionResult result;
        try
        {
            // Codes only exist in this request; sessions and recent-task storage never receive them.
            result = await SendAsync<CreditRedemptionResult>(HttpMethod.Post, "v1/redemptions", new { code = normalized }, RequireSession(), token).ConfigureAwait(false);
        }
        catch (CreditServiceException error) when (error.StatusCode == HttpStatusCode.BadRequest)
        { throw new CreditServiceException("兑换码不可用，请确认兑换码及有效期；本次未扣积分。", error.StatusCode); }
        catch (CreditServiceException error) when (error.StatusCode == HttpStatusCode.Conflict)
        { throw new CreditServiceException("账户余额已达上限，暂时无法兑换，请联系服务运营者。", error.StatusCode); }
        catch (CreditServiceException error) when (error.IsTransient)
        { throw new CreditServiceException("兑换结果尚未确认。请刷新余额，或在同一账户重新输入此兑换码并重试；不会重复入账。", error.StatusCode, isTransient: true); }
        if (result.Credits <= 0 || result.Balance < 0 || result.Reserved < 0 || result.Reserved > result.Balance
            || string.IsNullOrWhiteSpace(result.Reference) || result.Reference.Length > 128 || result.Reference.Any(char.IsControl))
            throw InvalidResponse();
        return result;
    }

    public Task<CreditOrder> CreateOrderAsync(string planId, Guid requestId, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(planId) || planId.Length > 128 || planId.Any(char.IsControl) || requestId == Guid.Empty)
            throw new InvalidOperationException("请选择有效次数包。");
        return SendAsync<CreditOrder>(HttpMethod.Post, "v1/orders", new { planId, requestId }, RequireSession(), token);
    }

    public async Task<CreditCheckoutQuote> QuoteAsync(string planId, string couponCode, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(planId) || planId.Length > 128 || planId.Any(char.IsControl))
            throw new InvalidOperationException("请选择有效积分包。");
        couponCode = CheckoutCoupon(couponCode);
        var quote = await CouponRequest<CreditCheckoutQuote>("v1/checkout/quote", new { planId, couponCode }, token).ConfigureAwait(false);
        if (quote.PlanId != planId || quote.Credits <= 0 || quote.AmountFen < 1 || quote.OriginalAmountFen <= quote.AmountFen
            || quote.DiscountFen != quote.OriginalAmountFen - quote.AmountFen)
            throw new InvalidOperationException("服务返回的优惠报价无效。请重新查看优惠价。");
        return quote;
    }

    public async Task<CreditOrder> CreateCouponOrderAsync(CreditCheckoutQuote quote, string couponCode, Guid requestId, CancellationToken token = default)
    {
        if (requestId == Guid.Empty) throw new InvalidOperationException("订单编号无效。");
        couponCode = CheckoutCoupon(couponCode);
        var order = await CouponRequest<CreditOrder>("v1/checkout/orders", new { planId = quote.PlanId, requestId, couponCode, expectedAmountFen = quote.AmountFen }, token).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(order.Id) || order.Id.Length > 128 || order.Id.Any(char.IsControl)) throw InvalidResponse();
        if (order.PlanId != quote.PlanId || string.IsNullOrWhiteSpace(order.Status)
            || order.Credits != quote.Credits || order.AmountFen != quote.AmountFen || order.OriginalAmountFen != quote.OriginalAmountFen
            || order.DiscountFen != quote.DiscountFen)
            throw new CreditOrderReviewException(order.Id, "订单与确认的优惠报价不一致。已阻止该订单付款，请联系运营者核对。");
        if (order.Status == "pending")
        {
            try { ValidatePaymentUrl(order.PayUrl ?? "", ServerUrl); }
            catch (InvalidOperationException) { throw new CreditOrderReviewException(order.Id, "订单付款链接无效。已阻止该订单付款，请联系运营者核对。"); }
        }
        return order;
    }

    private static string CheckoutCoupon(string couponCode)
    {
        if (string.IsNullOrWhiteSpace(couponCode) || couponCode.Length > 80 || couponCode.Any(char.IsControl))
            throw new InvalidOperationException("请输入有效的充值优惠码（最多 80 个字符）。");
        return couponCode.Trim();
    }

    private async Task<T> CouponRequest<T>(string path, object body, CancellationToken token)
    {
        try { return await SendAsync<T>(HttpMethod.Post, path, body, RequireSession(), token).ConfigureAwait(false); }
        catch (CreditServiceException e) when (e.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
        { throw new CreditServiceException("此积分服务暂不支持优惠码。若要按原价购买，请先清空优惠码。", e.StatusCode); }
        catch (CreditServiceException e) when (e.StatusCode == HttpStatusCode.BadRequest)
        { throw new CreditServiceException("这个优惠码当前无法用于所选积分包。请检查或更换优惠码。", e.StatusCode); }
        catch (CreditServiceException e) when (e.StatusCode == HttpStatusCode.Conflict)
        { throw new CreditServiceException("优惠价或订单状态已变化，请刷新订单并重新查看优惠价。", e.StatusCode); }
        catch (CreditServiceException e) when (e.IsTransient)
        { throw new CreditServiceException("暂时无法确认结果。请稍后重试，或刷新订单查看是否已建单。", e.StatusCode, isTransient: true); }
    }

    public Task<CreditOrder> GetOrderAsync(string orderId, bool refresh = false, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(orderId) || orderId.Length > 128 || orderId.Any(char.IsControl))
            throw new InvalidOperationException("订单编号无效。");
        return SendAsync<CreditOrder>(HttpMethod.Get, "v1/orders/" + Uri.EscapeDataString(orderId) + (refresh ? "?refresh=true" : ""), null, RequireSession(), token);
    }

    public async Task<CreditTranslationResult> ExecuteAsync(Guid id, string mode, string? text, byte[]? png, string target, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ValidateTranslation(id, mode, text, png, target);
        var session = RequireSession();
        sessions.RememberTask(ServerUrl, session.Token, id);
        object payload = new { mode, text, imageBase64 = png is null ? null : Convert.ToBase64String(png), targetLanguage = target.Trim() };
        bool submitted = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        try
        {
            CreditTranslationResult? result = null;
            for (int attempt = 0; attempt < 3 && result is null; attempt++)
            {
                submitted = true;
                try { result = await ReadTaskAsync(HttpMethod.Put, id, payload, session, deadline.Token).ConfigureAwait(false); }
                catch (CreditServiceException error) when (error.IsTransient)
                {
                    // The server may already have reserved credits. Query this exact ID before resending its idempotent PUT.
                    try { result = await QueryWithRetryAsync(id, session, deadline.Token).ConfigureAwait(false); }
                    catch (CreditServiceException missing) when (missing.StatusCode == HttpStatusCode.NotFound) { }
                }
            }
            if (result is null) throw Interrupted();
            while (!result.IsTerminal)
            {
                await Task.Delay(pollInterval, deadline.Token).ConfigureAwait(false);
                result = await QueryWithRetryAsync(id, session, deadline.Token).ConfigureAwait(false);
            }
            return result;
        }
        catch (OperationCanceledException)
        {
            if (submitted) await CancelBestEffortAsync(id, session).ConfigureAwait(false);
            if (token.IsCancellationRequested) throw new OperationCanceledException(token);
            throw new InvalidOperationException("等待积分翻译超时，已尝试取消。可在账户窗口查询最近任务；已完成的费用不会回滚。");
        }
    }

    public Task<CreditTranslationResult> GetTaskAsync(Guid id, CancellationToken token = default)
    {
        if (id == Guid.Empty) throw new InvalidOperationException("翻译任务编号无效。");
        return QueryWithRetryAsync(id, RequireSession(), token);
    }

    /// <summary>Queries the saved task without creating or charging for a new translation.</summary>
    public Task<CreditTranslationResult> ResumeLatestAsync(CancellationToken token = default)
    {
        var session = RequireSession();
        if (session.RecentTaskId is not Guid id || id == Guid.Empty)
            throw new InvalidOperationException("当前账户还没有可恢复的翻译任务。");
        return QueryWithRetryAsync(id, session, token);
    }

    public Task<CreditTranslationResult> CancelAsync(Guid id, CancellationToken token = default)
    {
        if (id == Guid.Empty) throw new InvalidOperationException("翻译任务编号无效。");
        return ReadTaskAsync(HttpMethod.Post, id, null, RequireSession(), token, cancel: true);
    }

    private async Task CancelBestEffortAsync(Guid id, CreditSession session)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await ReadTaskAsync(HttpMethod.Post, id, null, session, timeout.Token, cancel: true).ConfigureAwait(false); }
        catch { /* Cancellation is best effort. Never hide the original cancellation or create a replacement task. */ }
    }

    private async Task<CreditTranslationResult> QueryWithRetryAsync(Guid id, CreditSession session, CancellationToken token)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { return await ReadTaskAsync(HttpMethod.Get, id, null, session, token).ConfigureAwait(false); }
            catch (CreditServiceException error) when (error.IsTransient && attempt < 2)
            { await Task.Delay(pollInterval, token).ConfigureAwait(false); }
        }
    }

    private async Task<CreditTranslationResult> ReadTaskAsync(HttpMethod method, Guid id, object? body, CreditSession session, CancellationToken token, bool cancel = false)
    {
        var result = await SendAsync<CreditTranslationResult>(method, "v1/translations/" + id.ToString("D") + (cancel ? "/cancel" : ""), body, session, token).ConfigureAwait(false);
        if (result.Id != id || result.Status is not ("queued" or "running" or "succeeded" or "no_text" or "failed" or "cancelled")
            || result.SourceText?.Length > 100_000 || result.Translation?.Length > 100_000)
            throw InvalidResponse();
        // The retained billing record can outlive the original text. Expiry must not look like an empty success.
        result.IsExpired = result.Error == "result_expired";
        if (result.IsExpired)
        {
            result.SourceText = result.Translation = "";
            result.Error = "原文和译文已超过服务端保存期，无法恢复；任务及扣费记录仍保留。";
        }
        // Never display arbitrary provider diagnostics, which can contain credentials or request contents.
        else result.Error = result.Status == "failed" ? "积分翻译失败，请检查账户状态或联系服务管理员。" : "";
        if (result.Status == "succeeded" && !result.IsExpired && string.IsNullOrWhiteSpace(result.Translation)) throw InvalidResponse();
        return result;
    }

    private CreditSession RequireSession()
    {
        var session = sessions.Load(ServerUrl);
        if (session is null || session.IsExpired) throw new InvalidOperationException("请先打开积分账户窗口并登录当前服务。");
        return session;
    }

    private static void ValidateTranslation(Guid id, string mode, string? text, byte[]? png, string target)
    {
        if (id == Guid.Empty || mode is not ("text" or "image" or "handwriting")) throw new InvalidOperationException("积分翻译任务参数无效。");
        if (string.IsNullOrWhiteSpace(target) || target.Length > 100 || target.Any(char.IsControl)) throw new InvalidOperationException("请填写有效的目标语言。");
        if (mode == "text" && (string.IsNullOrWhiteSpace(text) || text.Length > MaxTextCharacters || png is not null))
            throw new InvalidOperationException("文本任务需提供不超过 16,000 字符的原文，且不能附带截图。");
        if (mode != "text" && (png is null || png.Length == 0 || png.Length > MaxImageBytes || !string.IsNullOrEmpty(text)))
            throw new InvalidOperationException("图片任务需提供不超过 12 MB 的 PNG 截图。");
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CreditSession? session, CancellationToken token,
        bool allowEmpty = false, HttpStatusCode? expectedStatus = null)
    {
        token.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var uri = new Uri(new Uri(ServerUrl), path);
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (session is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
        if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400)
                throw new CreditServiceException("积分服务返回了重定向，已阻止跳转。请核对服务地址。", response.StatusCode);
            if (!response.IsSuccessStatusCode)
                throw new CreditServiceException(StatusMessage(response.StatusCode), response.StatusCode,
                    response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500);
            if (expectedStatus is not null && response.StatusCode != expectedStatus) throw InvalidResponse();
            if (response.Content.Headers.ContentLength > MaxResponseBytes) throw InvalidResponse();
            using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var output = new MemoryStream();
            byte[] buffer = new byte[8192];
            int count;
            while ((count = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) != 0)
            {
                if (output.Length + count > MaxResponseBytes) throw InvalidResponse();
                output.Write(buffer, 0, count);
            }
            if (allowEmpty && output.Length == 0) return default!;
            return JsonSerializer.Deserialize<T>(output.ToArray(), Json) ?? throw InvalidResponse();
        }
        catch (JsonException) { throw InvalidResponse(); }
        catch (HttpRequestException) { throw Interrupted(); }
        catch (IOException) { throw Interrupted(); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw Interrupted(); }
    }

    private static string StatusMessage(HttpStatusCode code) => code switch
    {
        HttpStatusCode.BadRequest => "请求参数无效，请检查填写内容。（400）",
        HttpStatusCode.Unauthorized => "积分账户未登录或登录已失效，请重新登录。（401）",
        HttpStatusCode.Forbidden => "当前账户没有执行此操作的权限。（403）",
        HttpStatusCode.NotFound => "未找到对应任务或订单。（404）",
        HttpStatusCode.Conflict => "请求与已有记录冲突，请刷新账户或订单。（409）",
        HttpStatusCode.PaymentRequired => "可用积分不足，请先购买次数包。（402）",
        HttpStatusCode.TooManyRequests => "请求过于频繁，请稍后重试。（429）",
        _ => "积分服务暂时无法处理请求。（HTTP " + (int)code + "）"
    };
    private static CreditServiceException InvalidResponse() => new("积分服务响应格式无效，未使用返回内容。");
    private static CreditServiceException Interrupted() => new("积分服务连接中断。翻译任务可在账户窗口按原编号恢复，请勿重复发起新任务。", isTransient: true);
    public void Dispose() => http.Dispose();

    private sealed class LoginResponse
    {
        public string Token { get; set; } = "";
        public string Username { get; set; } = "";
        public DateTimeOffset ExpiresAt { get; set; }
    }
}

public sealed class CreditServiceException : InvalidOperationException
{
    public HttpStatusCode? StatusCode { get; }
    internal bool IsTransient { get; }
    internal CreditServiceException(string message, HttpStatusCode? statusCode = null, bool isTransient = false) : base(message)
    { StatusCode = statusCode; IsTransient = isTransient; }
}

internal sealed class CreditOrderReviewException(string orderId, string message) : InvalidOperationException(message)
{
    internal string OrderId { get; } = orderId;
}

public sealed class CreditTranslationResult
{
    public Guid Id { get; set; }
    public string Status { get; set; } = "";
    public string? SourceText { get; set; }
    public string? Translation { get; set; }
    public string? Error { get; set; }
    public long Balance { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public bool IsExpired { get; internal set; }
    [System.Text.Json.Serialization.JsonIgnore] public bool IsTerminal => Status is "succeeded" or "no_text" or "failed" or "cancelled";
}
public sealed class CreditAccount
{
    public string Username { get; set; } = "";
    public long Balance { get; set; }
    public long Reserved { get; set; }
}
public sealed class CreditRedemptionResult
{
    public int Credits { get; set; }
    public long Balance { get; set; }
    public long Reserved { get; set; }
    public bool AlreadyRedeemed { get; set; }
    public string Reference { get; set; } = "";
}
public sealed class CreditLedgerEntry
{
    public string Kind { get; set; } = "";
    public long Amount { get; set; }
    public long Balance { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string Reference { get; set; } = "";
}
public sealed class CreditPlan
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public long Credits { get; set; }
    public long AmountFen { get; set; }
    public override string ToString() => $"{Name} · {Credits} 积分 · ¥{AmountFen / 100m:0.00}";
}
public sealed class CreditOrder
{
    public string Id { get; set; } = "";
    public string? PlanId { get; set; }
    public string Status { get; set; } = "";
    public string? PayUrl { get; set; }
    public long Credits { get; set; }
    public long AmountFen { get; set; }
    public long? OriginalAmountFen { get; set; }
    public long? DiscountFen { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
public sealed class CreditCheckoutQuote
{
    public string PlanId { get; init; } = "";
    public int Credits { get; init; }
    public int AmountFen { get; init; }
    public int OriginalAmountFen { get; init; }
    public int DiscountFen { get; init; }
}
