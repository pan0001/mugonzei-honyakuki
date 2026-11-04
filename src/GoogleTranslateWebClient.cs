using System.Diagnostics;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace SteamVrTranslator;

internal interface IGoogleTranslateWebSession : IDisposable
{
    Task<string> TranslateAsync(string source, string target, CancellationToken token);
    Task ShowAsync(CancellationToken token);
    Task SavePreviewAsync(string path, CancellationToken token);
}

internal sealed record WebTranslationSnapshot(long RequestId, string Source, string Target, string Text, bool IsBlocked);

/// <summary>Uses the ordinary Google Translate webpage in its own, lazily created browser profile.</summary>
public sealed class GoogleTranslateWebClient : IDisposable
{
    internal const int MaxInputCharacters = 5000;
    internal const int MaxOutputCharacters = 32000;
    internal const string BlockedMessage = "谷歌网页要求验证或拒绝了本次请求，已停止自动翻译。请打开谷歌网页查看，或切换翻译服务。";
    private readonly IGoogleTranslateWebSession session;
    private readonly TimeSpan timeout;
    private readonly SemaphoreSlim serial = new(1, 1);
    private readonly CancellationTokenSource shutdown = new();
    private int disposed;

    public GoogleTranslateWebClient() : this(new StaWebSession()) { }

    internal GoogleTranslateWebClient(IGoogleTranslateWebSession session, TimeSpan? timeout = null)
    {
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        this.timeout = timeout ?? TimeSpan.FromSeconds(60);
        if (this.timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    public async Task<string> TranslateAsync(string text, AppSettings settings, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(settings);
        token.ThrowIfCancellationRequested();
        ValidateInput(text);
        if (string.IsNullOrWhiteSpace(text)) return "";
        string target = GoogleTranslateClient.NormalizeTargetLanguage(settings.TargetLanguage);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, shutdown.Token);
        deadline.CancelAfter(timeout);
        bool entered = false;
        try
        {
            await serial.WaitAsync(deadline.Token).ConfigureAwait(false);
            entered = true;
            string result = await session.TranslateAsync(NormalizeSource(text), target, deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(result))
                throw new InvalidOperationException("谷歌网页没有返回可核对的译文，请打开网页查看或切换翻译服务。");
            if (result.Length > MaxOutputCharacters)
                throw new InvalidOperationException("谷歌网页译文超过 32,000 个字符，请缩小截图范围。");
            return result;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && !shutdown.IsCancellationRequested)
        {
            throw new TimeoutException("谷歌网页翻译超时（60 秒），本次已停止。请打开网页查看网络或验证提示。");
        }
        finally { if (entered) serial.Release(); }
    }

    public async Task ShowBrowserAsync()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
        deadline.CancelAfter(timeout);
        try { await session.ShowAsync(deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!shutdown.IsCancellationRequested)
        { throw new TimeoutException("打开谷歌网页超时，请检查 WebView2 运行时或网络。"); }
    }

    internal async Task SavePreviewAsync(string path)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
        deadline.CancelAfter(timeout);
        await session.SavePreviewAsync(path, deadline.Token).ConfigureAwait(false);
    }

    internal static void ValidateInput(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaxInputCharacters)
            throw new InvalidOperationException("谷歌网页单次最多翻译 5,000 个字符，请缩小截图范围；本次未发送。");
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]))
            {
                if (++i >= text.Length || !char.IsLowSurrogate(text[i]))
                    throw new InvalidOperationException("识别文字含无效字符，无法发送到谷歌网页。");
            }
            else if (char.IsLowSurrogate(text[i]) || (char.IsControl(text[i]) && text[i] is not '\r' and not '\n' and not '\t'))
                throw new InvalidOperationException("识别文字含无效控制字符，无法发送到谷歌网页。");
        }
    }

    internal static string NormalizeSource(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    internal static Uri BuildPageUri(string targetLanguage) => new("https://translate.google.com/?sl=auto&tl="
        + Uri.EscapeDataString(GoogleTranslateClient.NormalizeTargetLanguage(targetLanguage)) + "&hl=en&op=translate");

    internal static bool IsAllowedNavigation(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0
        && uri.IdnHost.Equals("translate.google.com", StringComparison.OrdinalIgnoreCase);

    // visibleText must exclude ordinary source/translation content. The browser only supplies it
    // when the source editor is absent, so translating these phrases does not classify a normal page as blocked.
    internal static bool IsBlockedPage(string url, string visibleText, bool hasChallenge)
    {
        if (hasChallenge) return true;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0)
        {
            if (uri.IdnHost.Equals("consent.google.com", StringComparison.OrdinalIgnoreCase)) return true;
            if ((uri.IdnHost.Equals("www.google.com", StringComparison.OrdinalIgnoreCase)
                || uri.IdnHost.Equals("google.com", StringComparison.OrdinalIgnoreCase)
                || uri.IdnHost.Equals("translate.google.com", StringComparison.OrdinalIgnoreCase))
                && uri.AbsolutePath.StartsWith("/sorry", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return visibleText.Contains("automated queries", StringComparison.OrdinalIgnoreCase)
            || visibleText.Contains("unusual traffic", StringComparison.OrdinalIgnoreCase)
            || visibleText.Contains("verify you are human", StringComparison.OrdinalIgnoreCase)
            || visibleText.Contains("确认您不是机器人", StringComparison.Ordinal)
            || visibleText.Contains("异常流量", StringComparison.Ordinal);
    }

    internal static bool MatchesResult(WebTranslationSnapshot snapshot, long requestId, string source, string target) =>
        snapshot.RequestId == requestId && !snapshot.IsBlocked
        && snapshot.Source is not null && NormalizeSource(snapshot.Source).Equals(NormalizeSource(source), StringComparison.Ordinal)
        && string.Equals(snapshot.Target, target, StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(snapshot.Text) && snapshot.Text.Length <= MaxOutputCharacters;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        shutdown.Cancel();
        session.Dispose();
        // In-flight calls can still inspect shutdown and release serial while their cancellation unwinds.
    }

    private sealed class StaWebSession : IGoogleTranslateWebSession
    {
        private readonly object gate = new();
        private readonly CancellationTokenSource lifetime = new();
        private Task<BrowserHost>? ready;
        private BrowserHost? host;
        private bool disposed;

        private Task<BrowserHost> EnsureStarted()
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (ready is not null) return ready;
                var started = new TaskCompletionSource<BrowserHost>(TaskCreationOptions.RunContinuationsAsynchronously);
                ready = started.Task;
                var thread = new Thread(() =>
                {
                    try
                    {
                        using var browser = new BrowserHost();
                        _ = browser.Handle; // Disposal can post a close even before Application.Run begins.
                        lock (gate)
                        {
                            if (disposed) { started.TrySetCanceled(lifetime.Token); return; }
                            host = browser;
                        }
                        browser.Shown += async (_, _) =>
                        {
                            try
                            {
                                lifetime.Token.ThrowIfCancellationRequested();
                                await browser.InitializeAsync();
                                lifetime.Token.ThrowIfCancellationRequested();
                                started.TrySetResult(browser);
                            }
                            catch (OperationCanceledException) { started.TrySetCanceled(lifetime.Token); browser.CloseForShutdown(); }
                            catch (Exception)
                            {
                                started.TrySetException(new InvalidOperationException("无法启动谷歌网页。请安装或修复 Microsoft Edge WebView2 运行时后重启程序。"));
                                browser.CloseForShutdown();
                            }
                        };
                        Application.Run(browser);
                        started.TrySetCanceled();
                    }
                    catch (Exception)
                    {
                        started.TrySetException(new InvalidOperationException("无法启动谷歌网页后台窗口，请检查 WebView2 运行时后重启程序。"));
                    }
                    finally { lock (gate) host = null; }
                }) { IsBackground = true, Name = "Google Translate WebView2" };
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                return ready;
            }
        }

        private async Task<T> InvokeAsync<T>(Func<BrowserHost, Task<T>> action, CancellationToken token)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
            CancellationToken operationToken = linked.Token;
            var browser = await EnsureStarted().WaitAsync(operationToken).ConfigureAwait(false);
            operationToken.ThrowIfCancellationRequested();
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                browser.BeginInvoke((Action)(async () =>
                {
                    try
                    {
                        operationToken.ThrowIfCancellationRequested();
                        T result = await action(browser);
                        operationToken.ThrowIfCancellationRequested();
                        completion.TrySetResult(result);
                    }
                    catch (OperationCanceledException) { completion.TrySetCanceled(); }
                    catch (InvalidOperationException error) { completion.TrySetException(error); }
                    catch (Exception)
                    { completion.TrySetException(new InvalidOperationException("谷歌网页操作失败，请打开网页查看，或重启程序。")); }
                }));
            }
            catch (Exception) when (operationToken.IsCancellationRequested || browser.IsDisposed)
            { throw new OperationCanceledException(operationToken); }
            return await completion.Task.WaitAsync(operationToken).ConfigureAwait(false);
        }

        public Task<string> TranslateAsync(string source, string target, CancellationToken token) =>
            InvokeAsync(browser => browser.TranslateAsync(source, target, token), token);

        public async Task ShowAsync(CancellationToken token) => await InvokeAsync(browser =>
        {
            browser.ShowToUser();
            return Task.FromResult(true);
        }, token).ConfigureAwait(false);

        public async Task SavePreviewAsync(string path, CancellationToken token) => await InvokeAsync(async browser =>
        {
            await browser.SavePreviewAsync(path, token);
            return true;
        }, token).ConfigureAwait(false);

        public void Dispose()
        {
            BrowserHost? existing;
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                existing = host;
            }
            lifetime.Cancel();
            if (existing is null) return;
            try { existing.BeginInvoke((Action)existing.CloseForShutdown); }
            catch (InvalidOperationException) { }
        }
    }

    private sealed class BrowserHost : Form
    {
        private readonly WebView2 view = new() { Dock = DockStyle.Fill };
        private CoreWebView2 core = null!;
        private bool closing;
        private bool shownToUser;
        private long nextRequest;
        private long activeRequest;
        private TaskCompletionSource<string>? failure;
        private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
        protected override bool ShowWithoutActivation => !shownToUser;

        public BrowserHost()
        {
            Text = "Google 翻译网页 — 无言势翻译器";
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-30000, -30000);
            ClientSize = new Size(1100, 800);
            Opacity = 0;
            ShowInTaskbar = false;
            Controls.Add(view);
            FormClosing += (_, e) =>
            {
                if (closing) return;
                e.Cancel = true;
                shownToUser = false;
                Opacity = 0;
                ShowInTaskbar = false;
                Location = new Point(-30000, -30000);
            };
        }

        public async Task InitializeAsync()
        {
            string profile = Path.Combine(SettingsStore.DirectoryPath, "GoogleWebView2");
            Directory.CreateDirectory(profile);
            var environment = await CoreWebView2Environment.CreateAsync(null, profile);
            if (closing) throw new OperationCanceledException();
            await view.EnsureCoreWebView2Async(environment);
            if (closing) throw new OperationCanceledException();
            core = view.CoreWebView2;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.DownloadStarting += (_, e) => e.Cancel = true;
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.ProcessFailed += (_, _) => Fail("谷歌网页浏览器进程已停止，请重启程序后再试。");
            core.NavigationStarting += (_, e) =>
            {
                if (IsBlockedPage(e.Uri, "", false)) { Fail(BlockedMessage); return; }
                if (!IsAllowedNavigation(e.Uri))
                {
                    e.Cancel = true;
                    Fail("谷歌网页跳转到了未允许的地址，已停止本次翻译。请切换服务或检查网络。");
                }
            };
            core.WebResourceResponseReceived += (_, e) =>
            {
                if (e.Response.StatusCode is 403 or 429 && Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri)
                    && (uri.IdnHost.Equals("translate.google.com", StringComparison.OrdinalIgnoreCase)
                        || uri.IdnHost.Equals("www.google.com", StringComparison.OrdinalIgnoreCase))) Fail(BlockedMessage);
            };
        }

        private void Fail(string message) => failure?.TrySetResult(message);

        private async Task<T> CheckAwait<T>(Task<T> task, TaskCompletionSource<string> requestFailure, CancellationToken token)
        {
            await Task.WhenAny(task, requestFailure.Task).WaitAsync(token);
            token.ThrowIfCancellationRequested();
            if (requestFailure.Task.IsCompleted) throw new InvalidOperationException(await requestFailure.Task);
            return await task.WaitAsync(token);
        }

        private async Task NavigateAsync(Uri page, TaskCompletionSource<string> requestFailure, CancellationToken token)
        {
            var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            ulong? navigation = null;
            void Started(object? sender, CoreWebView2NavigationStartingEventArgs args) { navigation ??= args.NavigationId; }
            void Finished(object? sender, CoreWebView2NavigationCompletedEventArgs args)
            {
                if (navigation != args.NavigationId) return;
                if (args.HttpStatusCode is 403 or 429) { requestFailure.TrySetResult(BlockedMessage); return; }
                if (!args.IsSuccess) requestFailure.TrySetResult("谷歌网页加载失败，请检查网络或打开网页查看。");
                else completed.TrySetResult(true);
            }
            core.NavigationStarting += Started;
            core.NavigationCompleted += Finished;
            try
            {
                core.Navigate(page.AbsoluteUri);
                await CheckAwait(completed.Task, requestFailure, token);
            }
            finally
            {
                core.NavigationStarting -= Started;
                core.NavigationCompleted -= Finished;
            }
        }

        public async Task<string> TranslateAsync(string source, string target, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            long id = ++nextRequest;
            activeRequest = id;
            var requestFailure = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            failure = requestFailure;
            try
            {
                core.Stop();
                await NavigateAsync(BuildPageUri(target), requestFailure, token);
                // A fresh, empty document is required before entering text: old results cannot be reused.
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    string stateJson = await CheckAwait(core.ExecuteScriptAsync(ReadyScript), requestFailure, token);
                    var state = JsonSerializer.Deserialize<PageState>(stateJson, JsonOptions)
                        ?? throw new InvalidOperationException("无法读取谷歌网页状态，请打开网页查看。");
                    if (IsBlockedPage(core.Source, state.PageText ?? "", state.HasChallenge))
                        throw new InvalidOperationException(BlockedMessage);
                    if (!IsAllowedNavigation(core.Source)) throw new InvalidOperationException("谷歌网页地址发生变化，已停止本次翻译。");
                    if (state.HasSource)
                    {
                        if (!string.IsNullOrEmpty(state.Source) || state.HasTranslation)
                            throw new InvalidOperationException("谷歌网页保留了上一次内容，已停止本次翻译。请打开网页清空内容后再试。");
                        break;
                    }
                    await Task.Delay(200, token);
                }
                string inputScript = "(() => { const s=document.querySelector('textarea[aria-label=\"Source text\"][role=\"combobox\"]');"
                    + "if(!s || s.value || new URL(location.href).searchParams.get('tl')!==" + JsonSerializer.Serialize(target) + ")return false;"
                    + "window.__steamVrTranslation={id:" + id + ",target:" + JsonSerializer.Serialize(target) + "};"
                    + "Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype,'value').set.call(s," + JsonSerializer.Serialize(source) + ");"
                    + "s.dispatchEvent(new Event('input',{bubbles:true,composed:true}));return true;})()";
                string entered = await CheckAwait(core.ExecuteScriptAsync(inputScript), requestFailure, token);
                if (entered != "true") throw new InvalidOperationException("谷歌网页输入框已变化，已停止本次翻译。请打开网页查看。");
                string previous = "";
                var stable = Stopwatch.StartNew();
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    string json = await CheckAwait(core.ExecuteScriptAsync(SnapshotScript), requestFailure, token);
                    if (json.Length > 300_000) throw new InvalidOperationException("谷歌网页响应过大，请缩小截图范围。");
                    var state = JsonSerializer.Deserialize<PageSnapshot>(json, JsonOptions)
                        ?? throw new InvalidOperationException("无法读取谷歌网页译文，请打开网页查看。");
                    bool blocked = IsBlockedPage(core.Source, state.PageText ?? "", state.HasChallenge);
                    if (blocked) throw new InvalidOperationException(BlockedMessage);
                    if (!IsAllowedNavigation(core.Source)) throw new InvalidOperationException("谷歌网页地址发生变化，已停止本次翻译。");
                    if ((state.Text?.Length ?? 0) > MaxOutputCharacters)
                        throw new InvalidOperationException("谷歌网页译文超过 32,000 个字符，请缩小截图范围。");
                    var snapshot = new WebTranslationSnapshot(state.RequestId, state.Source ?? "", state.Target ?? "", state.Text ?? "", blocked);
                    if (activeRequest == id && MatchesResult(snapshot, id, source, target))
                    {
                        if (previous != snapshot.Text) { previous = snapshot.Text; stable.Restart(); }
                        else if (stable.ElapsedMilliseconds >= 800)
                        {
                            token.ThrowIfCancellationRequested();
                            return snapshot.Text;
                        }
                    }
                    else { previous = ""; stable.Restart(); }
                    await Task.Delay(150, token);
                }
            }
            finally
            {
                if (activeRequest == id)
                {
                    activeRequest = 0;
                    failure = null;
                    if (token.IsCancellationRequested && !closing)
                    {
                        try { core.Stop(); }
                        catch (Exception) { /* The browser may already have exited during shutdown. */ }
                    }
                }
            }
        }

        public void ShowToUser()
        {
            if (closing) throw new OperationCanceledException();
            shownToUser = true;
            ShowInTaskbar = true;
            Opacity = 1;
            if (Location.X < -10000 || Location.Y < -10000)
            {
                Rectangle area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 900);
                Location = new Point(area.Left + Math.Max(0, (area.Width - Width) / 2), area.Top + Math.Max(0, (area.Height - Height) / 2));
            }
            WindowState = FormWindowState.Normal;
            Show();
            Activate();
            if (activeRequest == 0 && (string.IsNullOrEmpty(core.Source) || core.Source == "about:blank"))
                core.Navigate(BuildPageUri("zh-CN").AbsoluteUri);
        }

        public async Task SavePreviewAsync(string path, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream).WaitAsync(token);
            token.ThrowIfCancellationRequested();
        }

        public void CloseForShutdown()
        {
            if (closing) return;
            closing = true;
            Fail("谷歌网页已经关闭。");
            try { if (view.CoreWebView2 is not null) view.CoreWebView2.Stop(); }
            catch (Exception) { /* Closing must also work after a failed browser process. */ }
            Close();
        }

        private sealed record PageState(bool HasSource, string? Source, bool HasTranslation, bool HasChallenge, string? PageText);
        private sealed record PageSnapshot(long RequestId, string? Source, string? Target, string? Text, bool HasChallenge, string? PageText);

        private const string DomPrelude = """
            const visible=e=>!!e && [...e.getClientRects()].some(r=>r.width>0&&r.height>0) && getComputedStyle(e).visibility!=='hidden' && getComputedStyle(e).display!=='none';
            const source=document.querySelector('textarea[aria-label="Source text"][role="combobox"]');
            const hasChallenge=[...document.querySelectorAll('iframe[src*="recaptcha"],iframe[src*="captcha"],form[action*="/sorry"],input[name="captcha"]')].some(visible);
            const pageText=source?'':(document.body?.innerText||'').slice(0,20000);
            const target=new URL(location.href).searchParams.get('tl')||'';
            const groups=[...document.querySelectorAll('span[lang]')].filter(e=>e.lang.toLowerCase()===target.toLowerCase()&&visible(e)&&e.querySelector('[jsname="W297wb"]'));
            const roots=groups.filter(e=>!groups.some(p=>p!==e&&p.contains(e)));
            const text=roots.map(e=>e.innerText).join('\n').slice(0,32001);
            """;
        private static readonly string ReadyScript = "(() => {" + DomPrelude
            + "return {hasSource:!!source,source:source?.value||'',hasTranslation:!!text.trim(),hasChallenge,pageText};})()";
        private static readonly string SnapshotScript = "(() => {" + DomPrelude
            + "return {requestId:window.__steamVrTranslation?.id||0,source:source?.value||'',target,text,hasChallenge,pageText};})()";
    }
}
