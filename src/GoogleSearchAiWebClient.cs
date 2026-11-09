using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace SteamVrTranslator;

/// <summary>Submits one image and prompt to a fresh Google Search AI Mode document.</summary>
public sealed partial class GoogleSearchAiWebClient : IDisposable
{
    internal const string EntryUrl = "https://www.google.com/aimode?hl=en";
    private const string LoginMessage = "Google 要求登录或同意使用条款。请打开 Google AI 模式网页手动完成，再关闭该窗口并重试。";
    private const string BlockedMessage = "Google 要求验证或限制了本次请求。请打开 Google AI 模式网页查看提示。";
    private const string PageMessage = "当前 Google 页面没有可用的 AI 模式图片输入。请打开网页检查所在地区、账号和图片入口。";
    private const string ChangedMessage = "Google AI 模式页面在操作过程中发生变化。请查看网页后重新截图。";
    private readonly string profile;
    private readonly Action<CoreWebView2>? configureForTest;
    private readonly object gate = new();
    private readonly CancellationTokenSource shutdown = new();
    private BrowserHost? host;
    private Task<BrowserHost>? starting;
    private bool disposed;

    public GoogleSearchAiWebClient() : this(Path.Combine(SettingsStore.DirectoryPath, "GoogleSearchAiWebView2")) { }
    internal GoogleSearchAiWebClient(string profileDirectory) : this(profileDirectory, null) { }
    internal GoogleSearchAiWebClient(string profileDirectory, Action<CoreWebView2>? configureForTest)
    {
        profile = Path.GetFullPath(profileDirectory);
        this.configureForTest = configureForTest;
    }

    public Task<string> TranslateImageAsync(byte[] png, string prompt, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        VisionImage.ValidatePng(png);
        if (string.IsNullOrWhiteSpace(prompt) || prompt.Length > 16_000)
            throw new InvalidOperationException("Google AI 模式的翻译提示为空或超过 16,000 字符。");
        return RunAsync((browser, ct) => browser.TranslateAsync(png, prompt, ct), TimeSpan.FromMinutes(2), token);
    }

    public Task ShowBrowserAsync() => RunAsync(async (browser, token) =>
    {
        await browser.ShowManualAsync(token);
        return true;
    }, TimeSpan.FromSeconds(45), CancellationToken.None);

    internal static bool IsAiPage(string? url)
    {
        if (!SecureGoogleUri(url, out var uri) || uri!.IdnHost != "www.google.com") return false;
        if (uri.AbsolutePath == "/aimode") return true;
        string key = uri.AbsolutePath == "/webhp" ? "aep" : "udm";
        string? expected = uri.AbsolutePath switch { "/webhp" => "11", "/search" => "50", _ => null };
        if (expected is null) return false;
        bool found = false;
        foreach (var field in uri.Query.TrimStart('?').Split('&'))
        {
            var parts = field.Split('=', 2);
            if (System.Net.WebUtility.UrlDecode(parts[0]) != key) continue;
            if (found || parts.Length != 2 || System.Net.WebUtility.UrlDecode(parts[1]) != expected) return false;
            found = true;
        }
        return found;
    }

    private static bool SecureGoogleUri(string? url, out Uri? uri) =>
        Uri.TryCreate(url, UriKind.Absolute, out uri) && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0;

    internal static bool IsManualPage(string? url) => SecureGoogleUri(url, out var uri) &&
        uri!.IdnHost is "www.google.com" or "accounts.google.com" or "consent.google.com";

    private static bool IsChallenge(Uri uri) => uri.IdnHost == "www.google.com" &&
        (uri.AbsolutePath == "/sorry" || uri.AbsolutePath.StartsWith("/sorry/", StringComparison.Ordinal));

    private static string? BlockReason(string url)
    {
        if (!SecureGoogleUri(url, out var uri)) return ChangedMessage;
        if (uri!.IdnHost is "accounts.google.com" or "consent.google.com") return LoginMessage;
        if (IsChallenge(uri)) return BlockedMessage;
        return IsAiPage(url) ? null : ChangedMessage;
    }

    private Task<BrowserHost> Start()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (starting is not null) return starting;
            var ready = new TaskCompletionSource<BrowserHost>(TaskCreationOptions.RunContinuationsAsynchronously);
            starting = ready.Task;
            var thread = new Thread(() =>
            {
                try
                {
                    using var browser = new BrowserHost(profile, configureForTest);
                    _ = browser.Handle;
                    lock (gate)
                    {
                        if (disposed) { ready.TrySetCanceled(); return; }
                        host = browser;
                    }
                    browser.Shown += (_, _) => ready.TrySetResult(browser);
                    Application.Run(browser);
                }
                catch (Exception) { ready.TrySetException(new InvalidOperationException("无法启动 Google AI 模式后台窗口。请检查 Microsoft Edge WebView2 运行时。")); }
                finally
                {
                    ready.TrySetCanceled();
                    lock (gate) { host = null; starting = null; }
                }
            }) { IsBackground = true, Name = "Google Search AI Mode WebView2" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return ready.Task;
        }
    }

    private async Task<T> RunAsync<T>(Func<BrowserHost, CancellationToken, Task<T>> action, TimeSpan timeout, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, shutdown.Token);
        deadline.CancelAfter(timeout);
        CancellationToken ct = deadline.Token;
        try
        {
            var browser = await Start().WaitAsync(ct).ConfigureAwait(false);
            var reply = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            browser.BeginInvoke((Action)(async () =>
            {
                try
                {
                    ct.ThrowIfCancellationRequested();
                    var result = await action(browser, ct);
                    ct.ThrowIfCancellationRequested();
                    reply.TrySetResult(result);
                }
                catch (OperationCanceledException) { reply.TrySetCanceled(ct); }
                catch (TimeoutException error) { reply.TrySetException(error); }
                catch (InvalidOperationException error) { reply.TrySetException(error); }
                catch (Exception) { reply.TrySetException(new InvalidOperationException("Google AI 模式网页操作失败，请打开网页检查连接或 WebView2 运行时。")); }
            }));
            return await reply.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && !shutdown.IsCancellationRequested)
        { throw new TimeoutException("等待 Google AI 模式网页超时。请打开网页检查登录、图片上传或回答状态。"); }
    }

    public void Dispose()
    {
        BrowserHost? browser;
        lock (gate) { if (disposed) return; disposed = true; browser = host; }
        shutdown.Cancel();
        if (browser is null) return;
        try { browser.BeginInvoke((Action)browser.Close); } catch (InvalidOperationException) { }
    }

    private sealed class BrowserHost : Form
    {
        private readonly string profilePath;
        private readonly Action<CoreWebView2>? configure;
        private readonly SemaphoreSlim serial = new(1, 1);
        private Task<CoreWebView2Environment>? environment;
        private Form? manual;

        internal BrowserHost(string profilePath, Action<CoreWebView2>? configure)
        {
            this.profilePath = profilePath;
            this.configure = configure;
            Text = "Google AI 模式后台";
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-30000, -30000);
            ClientSize = new Size(1160, 820);
            ShowInTaskbar = false;
            Opacity = 0;
            FormClosing += (_, _) => manual?.Close();
        }

        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams
        {
            get { var parameters = base.CreateParams; parameters.ExStyle |= 0x08000000; return parameters; } // WS_EX_NOACTIVATE
        }

        private async Task<CoreWebView2> InitializeView(WebView2 view, bool interactive, CancellationToken token)
        {
            try
            {
                Directory.CreateDirectory(profilePath);
                if (environment is null || environment.IsFaulted || environment.IsCanceled)
                    environment = CoreWebView2Environment.CreateAsync(null, profilePath, new CoreWebView2EnvironmentOptions { Language = "en-US" });
                var env = await environment.WaitAsync(token);
                await view.EnsureCoreWebView2Async(env).WaitAsync(token);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            { throw new InvalidOperationException("无法载入 Google AI 模式网页。请安装或修复 Microsoft Edge WebView2 运行时。"); }
            token.ThrowIfCancellationRequested();
            var core = view.CoreWebView2;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsWebMessageEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultScriptDialogsEnabled = interactive;
            core.DownloadStarting += (_, e) => e.Cancel = true;
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.LaunchingExternalUriScheme += (_, e) => e.Cancel = true;
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                if (interactive && e.IsUserInitiated && IsManualPage(e.Uri)) core.Navigate(e.Uri);
            };
            configure?.Invoke(core);
            return core;
        }

        internal async Task ShowManualAsync(CancellationToken token)
        {
            if (manual is { IsDisposed: false }) { manual.Activate(); return; }
            var window = new Form { Text = "Google AI 模式 · 登录与验证", Size = new Size(1080, 780), StartPosition = FormStartPosition.CenterScreen };
            manual = window;
            using var closed = new CancellationTokenSource();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, closed.Token);
            FormClosedEventHandler handleClose = (_, _) => { manual = null; closed.Cancel(); };
            window.FormClosed += handleClose;
            try
            {
                var view = new WebView2 { Dock = DockStyle.Fill, AllowExternalDrop = false };
                window.Controls.Add(view);
                window.Show();
                var core = await InitializeView(view, true, linked.Token);
                core.NavigationStarting += (_, e) => e.Cancel = !IsManualPage(e.Uri);
                core.Navigate(EntryUrl);
            }
            catch { window.Close(); throw; }
            finally
            {
                window.FormClosed -= handleClose;
                if (!window.IsDisposed) window.FormClosed += (_, _) => manual = null;
            }
        }

        internal async Task<string> TranslateAsync(byte[] png, string prompt, CancellationToken token)
        {
            await serial.WaitAsync(token);
            try
            {
                if (manual is { IsDisposed: false }) throw new InvalidOperationException("请先完成 Google 网页的手动操作并关闭该窗口，再截图翻译。");
                using var view = new WebView2 { Dock = DockStyle.Fill, AllowExternalDrop = false };
                Controls.Add(view);
                var core = await InitializeView(view, false, token);
                var page = new SearchPage(core);
                return await page.TranslateAsync(png, prompt, token);
            }
            finally { serial.Release(); }
        }
    }
}
