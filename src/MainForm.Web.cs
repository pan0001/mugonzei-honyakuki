using System.Drawing.Imaging;
using System.Text.Json;

namespace SteamVrTranslator;

internal sealed partial class MainForm
{
    private LocalWebSurface? web;
    private System.Windows.Forms.Timer? webChanges;
    private bool webReady, webDirty, needsWebSnapshot;
    private Image? projectedPreview;
    private string? thumbnailData;
    private bool encodingThumbnail, thumbnailSent;
    internal Func<Image, string> EncodeWorkspaceThumbnail = EncodeThumbnail;
    private readonly Func<CancellationToken,Task<GitHubUpdateResult>> readRelease;
    private readonly Action<Uri> openBrowser;
    private CancellationTokenSource? updateCheck;
    private GitHubUpdateResult? checkedRelease;
    private string updateState = "idle", updateMessage = "";
    internal Func<IReadOnlyList<DesktopWindowOption>> ReadDesktopWindows = WindowCaptureService.ListWindows;
    internal Func<string, bool> IsDesktopWindowSelectable = WindowCaptureService.IsSelectable;
    private HashSet<string> listedWindowIds = [];
    private bool listingDesktopWindows;

    private async Task ListWorkspaceWindows()
    {
        if (listingDesktopWindows) return;
        listingDesktopWindows = true;
        try
        {
            var windows = await Task.Run(ReadDesktopWindows);
            if (closing) return;
            listedWindowIds = windows.Select(window => window.Id).ToHashSet(StringComparer.Ordinal);
            web?.Post(new { type = "desktopWindows", windows });
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            listedWindowIds.Clear();
            if (!closing) web?.Post(new { type = "desktopWindows", error = true });
        }
        finally { listingDesktopWindows = false; }
    }

    private void ValidateWorkspaceWindow(AppSettings next)
    {
        if (next.DesktopCaptureSource != "Window" || (settings.DesktopCaptureSource == "Window"
            && next.DesktopWindowId == settings.DesktopWindowId)) return;
        if (!listedWindowIds.Contains(next.DesktopWindowId))
            throw new InvalidOperationException("所选截图窗口已不可用。请还原窗口、刷新列表并重新选择。");
    }

    private async Task CheckForUpdateAsync()
    {
        if (updateCheck is not null || closing) return;
        using var request = new CancellationTokenSource();
        updateCheck = request;
        checkedRelease = null;
        updateState = "checking";
        updateMessage = "正在连接 GitHub…";
        PushWorkspace();
        try
        {
            var release = await readRelease(request.Token);
            if (closing || request.IsCancellationRequested) return;
            checkedRelease = release;
            updateState = release.UpdateAvailable ? "available" : "current";
            updateMessage = !release.UpdateAvailable ? "" : release.InstallerUrl is null
                ? "此版本没有可直接下载的 Windows 安装程序，请查看 GitHub 发布页。"
                : "点击下载将在默认浏览器中打开 Windows 安装程序的下载地址。";
        }
        catch (OperationCanceledException) when (closing || request.IsCancellationRequested) { }
        catch (Exception e) when (e is InvalidOperationException or HttpRequestException or OperationCanceledException)
        {
            updateState = "error";
            updateMessage = e is InvalidOperationException ? e.Message : "暂时无法连接 GitHub，请重试。";
        }
        finally { updateCheck = null; QueueWebUpdate(); }
    }

    private void OpenCheckedUpdate()
    {
        // Only use destinations from the native check. The web message never supplies a URL.
        if (closing || checkedRelease is not { UpdateAvailable: true } r) return;
        try { openBrowser(r.InstallerUrl ?? r.ReleaseUrl); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            updateMessage = "无法打开默认浏览器，请检查浏览器设置后重试。";
        }
    }
    private readonly Dictionary<string,object?> sent = [];
    private string ReadWebAccountName()
    {
        if (demo) return "";
        try { return new CreditSessionStore().Load(settings.CreditServerUrl) is { IsExpired: false } session ? session.Username : ""; }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException) { return ""; }
    }
    private void SendAccountIdentity()
    {
        string username = ReadWebAccountName();
        web?.Post(new { type = "state", accountUsername = username, accountLoggedIn = username.Length > 0 });
    }

    private void BuildWebWorkspace(string? profile)
    {
        Size = new Size(Math.Min(1150, Screen.FromControl(this).WorkingArea.Width),
            Math.Min(800, Screen.FromControl(this).WorkingArea.Height));
        // The capture pipeline also supplies the tray and headset. Keep its input/state adapters,
        // but do not build or show the old native pages under the browser.
        codexSettingsCard = new(); apiSettingsCard = new(); googleSettingsCard = new();
        creditSettingsCard = new(); localOcrSettingsCard = new(); vrInputSettings = new();
        InitializeSettingsControls();
        web = new LocalWebSurface("workspace.html", ReceiveWorkspace, profile);
        Controls.Add(web);
        WindowChrome.Apply(this);
        webChanges = new() { Interval = 70 };
        webChanges.Tick += (_, _) => { webChanges.Stop(); PushWorkspace(); };
        foreach (var c in new Control[] { status, vrStatus, workspaceInputHelp, imageBox, sourceText,
            translatedText, recognizeButton, apiTestResult, googleTestResult, creditBalanceNote, localOcrStatus, translateButton })
            c.TextChanged += (_, _) => QueueWebUpdate();
        cancelButton.EnabledChanged += (_, _) => QueueWebUpdate();
        VisibleChanged += (_, _) => QueueWebUpdate();
    }

    private void QueueWebUpdate()
    {
        if (webReady && Visible && !closing && webChanges is { Enabled: false }) webChanges.Start();
    }

    private void PushWorkspace(bool full = false)
    {
        if (web is null || !webReady || closing || !Visible) return;
        full |= needsWebSnapshot;
        needsWebSnapshot = false;
        if (full) sent.Clear();
        var delta = new Dictionary<string,object?> { ["type"] = "state" };
        void Put(string key, object? value)
        {
            if (sent.TryGetValue(key, out var old) && Equals(old,value)) return;
            sent[key] = delta[key] = value;
        }
        Put("mode", settings.RuntimeMode); Put("effectiveMode", ActiveDesktopMode ? "Desktop" : "SteamVR");
        Put("vrStatus", vrStatus.Text); Put("status", status.Text); Put("source", sourceText.Text); Put("sourceHint", sourceHint);
        Put("translation", translatedText.Text); Put("busy", busy);
        // If the bitmap changed, deliver the new capture number along with its own pixels.
        if (!full && thumbnailSent && ReferenceEquals(preview.Image, projectedPreview)) Put("captureLabel", imageBox.Text);
        Put("canCancel", cancelButton.Enabled); Put("canRecognize", !(settings.TranslationBackend == "Credits" && settings.ProcessingMode is "Image" or "Handwriting"));
        Put("recognizeLabel", recognizeButton.Text); Put("inputHelp", workspaceInputHelp.Text); Put("page", pages.Count > 0 ? pageIndex + 1 : 0);
        Put("pages", pages.Count); Put("apiTestResult", apiTestResult.Text); Put("googleTestResult", googleTestResult.Text);
        Put("localOcrStatus", localOcrStatus.Text); Put("creditNote", creditBalanceNote.Text);
        Put("testingApi", apiVisionCheck is not null); Put("testingGoogle", googleTranslationCheck is not null);
        Put("testingCodex", loginCheck is not null);
        Put("appVersion", GitHubUpdateChecker.CurrentVersion); Put("updateState", updateState);
        Put("updateMessage", updateMessage); Put("updateVersion", checkedRelease?.LatestVersion);
        Put("updateHasInstaller", checkedRelease?.InstallerUrl is not null);
        if (full)
        {
            delta["settings"] = WebSettingsPatch.Public(settings);
            delta["presets"] = ApiProviderPresets.All;
            string username = ReadWebAccountName();
            delta["accountUsername"] = username; delta["accountLoggedIn"] = username.Length > 0;
        }
        if (delta.Count > 1) web.Post(delta);
        if (full) thumbnailSent = false;
        if (encodingThumbnail) return;
        if (!ReferenceEquals(preview.Image, projectedPreview))
        {
            projectedPreview = preview.Image;
            thumbnailData = null;
            thumbnailSent = false;
            if (projectedPreview is not null)
            {
                _ = PrepareThumbnailAsync(projectedPreview);
                return;
            }
        }
        if (!thumbnailSent)
        {
            sent["captureLabel"] = imageBox.Text;
            web.Post(new { type = "state", preview = thumbnailData, captureLabel = imageBox.Text });
            thumbnailSent = true;
        }
    }

    private async Task PrepareThumbnailAsync(Image image)
    {
        encodingThumbnail = true;
        try
        {
            // Clone gives the worker an independent GDI+ image handle. Capture or shutdown
            // can replace/dispose the displayed image while that worker scales and compresses.
            var owned = (Image)image.Clone();
            string encoded = await Task.Run(() =>
            {
                using (owned) return EncodeWorkspaceThumbnail(owned);
            });
            if (ReferenceEquals(image, preview.Image)) thumbnailData = encoded;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.Runtime.InteropServices.ExternalException)
        { /* Publish an empty thumbnail for this frame if GDI+ cannot render it. */ }
        finally { encodingThumbnail = false; }
        // Only start the newest outstanding frame. A reload reuses its cached JPEG, and
        // a hidden workspace waits until it is restored before starting another encode.
        if (!closing && !IsDisposed) PushWorkspace();
    }

    internal static string EncodeThumbnail(Image image)
    {
        double factor = Math.Min(1, Math.Min(720d / image.Width, 480d / image.Height));
        using var thumb = new Bitmap(image, Math.Max(1, (int)(image.Width * factor)), Math.Max(1, (int)(image.Height * factor)));
        using var output = new MemoryStream();
        thumb.Save(output, ImageFormat.Jpeg);
        return "data:image/jpeg;base64," + Convert.ToBase64String(output.GetBuffer(), 0, checked((int)output.Length));
    }

    private async Task ReceiveWorkspace(JsonElement message)
    {
        if (closing || web is null) return;
        try
        {
            string? type = message.GetProperty("type").GetString();
            if (type == "ready") { webReady = true; webDirty = false; needsWebSnapshot = true; PushWorkspace(full: true); return; }
            if (!webReady || setupDialogOpen) return;
            switch (type)
            {
                case "dirty": webDirty = message.GetProperty("dirty").GetBoolean(); break;
                case "mode":
                    SetRuntimeMode(message.GetProperty("mode").GetString() ?? "");
                    break;
                case "save":
                    var next = WebSettingsPatch.Apply(settings, message.GetProperty("settings"));
                    ValidateWorkspaceWindow(next);
                    bool accountChanged = next.TranslationBackend == "Credits" &&
                        (settings.TranslationBackend != "Credits" || next.CreditServerUrl != settings.CreditServerUrl);
                    SaveSettings(next); LoadSettings(); webDirty = false;
                    web.Post(new { type = "saved", settings = WebSettingsPatch.Public(settings) });
                    if (!demo && accountChanged && Program.NeedsAccountSignIn(settings, new CreditSessionStore().Load))
                        OpenAccountPage(loginOnly: true);
                    SendAccountIdentity();
                    break;
                case "action":
                    string? action = message.GetProperty("action").GetString();
                    bool usingDraft = action is "testApi" or "testGoogle" or "checkCodex" or "account" or "setup" or "login";
                    var prior = settings;
                    if (usingDraft && message.TryGetProperty("settings", out var patch)) LoadEditorValues(WebSettingsPatch.Apply(settings, patch));
                    try
                    {
                        switch (action)
                        {
                            case "listDesktopWindows": await ListWorkspaceWindows(); break;
                            case "translate": await CaptureAndTranslate(false); break;
                            case "recognize": await CaptureAndTranslate(true); break;
                            case "cancel": CancelAllWork(); break;
                            case "saveScreenshot": SaveCurrentScreenshot(); break;
                            case "openVrcls":
                                try { openBrowser(new Uri("https://web.voicelinkvr.top/")); }
                                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
                                { throw new InvalidOperationException("无法打开默认浏览器，请检查浏览器设置。", error); }
                                break;
                            case "checkUpdate": await CheckForUpdateAsync(); break;
                            case "downloadUpdate": OpenCheckedUpdate(); break;
                            case "previous": ChangePage(-1); break;
                            case "next": ChangePage(1); break;
                            case "hideSubtitle": HideOverlay(); break;
                            case "testSubtitle": ShowDemo(); break;
                            case "setup": OpenSetupWizard(); break;
                            case "login": OpenAccountPage(loginOnly: true); break;
                            case "account": OpenCreditAccount(); break;
                            case "bindings":
                                if (!vr.IsConnected) throw new InvalidOperationException("请先手动打开 SteamVR 并连接头显。");
                                vr.OpenBindings(); break;
                            case "settingsFolder":
                                Directory.CreateDirectory(SettingsStore.DirectoryPath);
                                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(SettingsStore.DirectoryPath) { UseShellExecute = true }); break;
                            case "checkCodex": await CheckCodexLogin(); break;
                            case "testApi": if (apiVisionCheck is not null) RequestCancellation(apiVisionCheck); else await CheckApiVision(); break;
                            case "testGoogle": if (googleTranslationCheck is not null) RequestCancellation(googleTranslationCheck); else await CheckGoogleTranslate(); break;
                            case "googleBrowser": await ShowGoogleBrowser(); break;
                            case "reconnect": ConnectVr(); break;
                            default: throw new ArgumentException("不支持这个操作。");
                        }
                    }
                    finally
                    {
                        if (usingDraft && !closing)
                        {
                            LoadSettings();
                            if (action is "login" or "account" or "setup") SendAccountIdentity();
                            if (!ReferenceEquals(prior, settings) && action is "setup" or "account")
                            {
                                webDirty = false; web.Post(new { type = "saved", settings = WebSettingsPatch.Public(settings) });
                            }
                        }
                    }
                    break;
                default: throw new ArgumentException("不支持这个操作。");
            }
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or KeyNotFoundException or FormatException)
        {
            // Display locally authored validation errors. Never send request bodies, saved keys or stack traces.
            web.Post(new { type = "error", message = e is KeyNotFoundException or FormatException ? "页面请求格式无效。" : e.Message });
        }
        finally { QueueWebUpdate(); }
    }
}
