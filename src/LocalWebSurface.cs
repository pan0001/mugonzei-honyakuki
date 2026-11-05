using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System.Diagnostics;
using System.Text.Json;

namespace SteamVrTranslator;

/// <summary>One packaged page per surface. No server, network access or general-purpose native object bridge.</summary>
internal sealed class LocalWebSurface : UserControl
{
    private const string Host = "mugonzei.internal";
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Uri entry;
    private readonly Func<JsonElement, Task> receive;
    private readonly string profile;
    private readonly Label hint = new() { Text = "正在打开界面…", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter };
    private readonly FlowLayoutPanel recovery = new() { Dock = DockStyle.Bottom, AutoSize = true, Visible = false };
    private WebView2? view;
    private bool initializing, failed;
    public LocalWebSurface(string page, Func<JsonElement,Task> receive, string? profileDirectory = null)
    {
        if (page is not ("workspace.html" or "setup.html" or "account.html")) throw new ArgumentException("Unknown application page.");
        entry = new Uri("https://" + Host + "/" + page);
        this.receive = receive;
        profile = profileDirectory ?? Path.Combine(SettingsStore.DirectoryPath, "DesktopWebUI");
        Dock = DockStyle.Fill; BackColor = UiTheme.Background; ForeColor = UiTheme.Ink;
        var retry = new Button { Text = "重试", AutoSize = true };
        var install = new Button { Text = "打开微软运行时下载页", AutoSize = true };
        retry.Click += (_, _) => { failed = false; _ = InitializeAsync(); };
        install.Click += (_, _) => { try { Process.Start(new ProcessStartInfo("https://developer.microsoft.com/microsoft-edge/webview2/") { UseShellExecute = true }); } catch { } };
        recovery.Controls.AddRange([retry, install]); Controls.Add(hint); Controls.Add(recovery);
    }

    protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); ScheduleInit(); }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); ScheduleInit(); }
    private void ScheduleInit()
    {
        if (IsHandleCreated && Visible && !IsDisposed && !failed && view is null && !initializing)
            BeginInvoke((Action)(() => { if (!IsDisposed && Visible) _ = InitializeAsync(); }));
    }

    internal bool IsEntry(string? source) => Uri.TryCreate(source, UriKind.Absolute, out var u) && u == entry && u.UserInfo.Length == 0;
    private async Task InitializeAsync()
    {
        if (IsDisposed || initializing || view is not null) return;
        initializing = true;
        recovery.Visible = false;
        hint.Text = "正在打开界面…"; hint.Visible = true;
        try
        {
            string directory = Path.Combine(AppContext.BaseDirectory, "assets", "webui");
            if (!File.Exists(Path.Combine(directory, entry.AbsolutePath.TrimStart('/')))) throw new FileNotFoundException();
            var environment = await CoreWebView2Environment.CreateAsync(null, profile);
            if (IsDisposed || Disposing) return;
            var browser = view = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = UiTheme.Background,
                AllowExternalDrop = false };
            Controls.Add(browser); browser.BringToFront();
            await browser.EnsureCoreWebView2Async(environment);
            if (IsDisposed || Disposing || view != browser) return;
            var core = browser.CoreWebView2;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.IsSwipeNavigationEnabled = false;
            core.Settings.AreDefaultScriptDialogsEnabled = false;
            core.SetVirtualHostNameToFolderMapping(Host, directory, CoreWebView2HostResourceAccessKind.Deny);
            core.NavigationStarting += (_, e) => e.Cancel = !IsEntry(e.Uri);
            core.FrameNavigationStarting += (_, e) => e.Cancel = true;
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.DownloadStarting += (_, e) => e.Cancel = true;
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.LaunchingExternalUriScheme += (_, e) => e.Cancel = true;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
            core.WebResourceRequested += (_, e) =>
            {
                if (Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri) &&
                    ((uri.Scheme == "https" && uri.Authority == Host && uri.UserInfo.Length == 0) ||
                     (uri.Scheme == "data" && e.ResourceContext == CoreWebView2WebResourceContext.Image))) return;
                e.Response = environment.CreateWebResourceResponse(null, 403, "Forbidden", "");
            };
            core.WebMessageReceived += async (_, e) =>
            {
                if (IsDisposed || view != browser || !IsEntry(e.Source) || !IsEntry(core.Source)) return;
                try
                {
                    var raw = e.WebMessageAsJson;
                    if (raw.Length > 64 * 1024) return;
                    using var doc = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = 12 });
                    if (doc.RootElement.ValueKind == JsonValueKind.Object) await receive(doc.RootElement.Clone());
                }
                catch (JsonException) { Post(new { type = "error", message = "页面请求格式不正确。" }); }
                catch (Exception) { Post(new { type = "error", message = "操作没有完成，请重试。" }); }
            };
            core.NavigationCompleted += (_, e) => { if (!IsDisposed && view == browser && !e.IsSuccess && !e.WebErrorStatus.Equals(CoreWebView2WebErrorStatus.OperationCanceled)) Fail("无法打开本地界面文件，请重试或重新安装。"); };
            core.ProcessFailed += (_, e) => { if (!IsDisposed && view == browser && e.ProcessFailedKind is CoreWebView2ProcessFailedKind.BrowserProcessExited or CoreWebView2ProcessFailedKind.RenderProcessExited) Fail("界面进程已退出，请点击重试。"); };
            core.Navigate(entry.AbsoluteUri);
        }
        catch (Exception e) { if (!IsDisposed) Fail(e is FileNotFoundException ? "找不到界面文件，请完整安装程序。" : "无法启动内嵌界面。请安装或修复 Microsoft Edge WebView2 Runtime 后重试。"); }
        finally { initializing = false; }
    }
    private void Fail(string reason)
    {
        failed = true;
        var old = view; view = null;
        if (old is not null) { Controls.Remove(old); BeginInvoke((Action)old.Dispose); }
        hint.Text = reason; hint.Visible = recovery.Visible = true; hint.BringToFront(); recovery.BringToFront();
    }
    internal void Post(object payload)
    {
        if (IsDisposed || view?.CoreWebView2 is not { } core || !IsEntry(core.Source)) return;
        try { core.PostWebMessageAsJson(JsonSerializer.Serialize(payload, Json)); }
        catch (InvalidOperationException) { /* window is closing */ }
    }
    internal CoreWebView2? CoreForTests => view?.CoreWebView2;
}
