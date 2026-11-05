using System.Diagnostics;

namespace SteamVrTranslator;

internal sealed partial class MainForm : Form
{
    private static readonly Color Background = UiTheme.Background;
    private static readonly Color Surface = UiTheme.Card;
    private static readonly Color Ink = UiTheme.Ink;
    private static readonly Color Accent = UiTheme.Accent;
    private readonly VrService vr = new();
    private readonly OcrService ocr = new();
    private readonly LocalJapaneseOcrService localJapaneseOcr = new();
    private readonly TranslationService translator = new();
    private readonly System.Windows.Forms.Timer pollTimer = new() { Interval = 16 };
    private readonly NotifyIcon tray = new() { Text = "无言势翻译器", Icon = SystemIcons.Application };
    private readonly Label status = new() { AutoSize = true, ForeColor = Accent, Padding = new Padding(4, 8, 4, 8) };
    private readonly Label vrStatus = new() { AutoSize = true, ForeColor = Ink, MaximumSize = new Size(1000, 0) };
    private readonly TextBox sourceText = Readout();
    private readonly TextBox translatedText = Readout();
    private readonly PictureBox preview = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(10, 14, 21) };
    private readonly UiCard imageBox = new() { Text = "截图预览", Caption = "每次触发都会获取新的画面", Dock = DockStyle.Fill, Margin = new Padding(0, 0, 12, 14) };
    private readonly LatestCaptureRequests captureRequests = new();
    private readonly VrFrameGuard holdFrames = new(), selectionFrames = new(), expiryFrames = new();
    private long reconnectAt, healthyInputSince;
    private int reconnectAttempts;
    private readonly TextBox endpoint = Input(), model = Input(), apiKey = Input(), target = Input(), languages = Input(), hotkeyText = Input();
    private readonly TextBox codexPath = Input(), codexModel = Input();
    private readonly ComboBox codexReasoningEffort = new UiComboBox() { Name = "codexReasoningEffort", DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
    private readonly ComboBox backend = new UiComboBox() { Name = "translationBackend", DropDownStyle = ComboBoxStyle.DropDownList, Width = 230 };
    private readonly ComboBox processingMode = new UiComboBox() { Name = "processingMode", DropDownStyle = ComboBoxStyle.DropDownList, Width = 330 };
    private readonly ComboBox apiPreset = new UiComboBox() { Name = "apiPreset", DropDownStyle = ComboBoxStyle.DropDownList, Width = 340 };
    private readonly Button useApiPreset = Button("使用此地址");
    private readonly Label apiPresetNote = Note("");
    private readonly ComboBox apiImageDetail = new UiComboBox() { Name = "apiImageDetail", DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
    private readonly CheckBox apiStream = new() { Name = "apiStream", Text = "使用流式接口（部分模型需要）", AutoSize = true };
    private readonly Button checkApiVision = Button("测试图片识别");
    private readonly Label apiTestResult = Note("");
    private readonly Button checkCodexLogin = Button("检测 Codex 登录");
    private readonly Label dataNote = Note("");
    private readonly Label localOcrStatus = Note("");
    private readonly Button checkGoogleTranslate = Button("测试谷歌翻译");
    private readonly Button showGoogleBrowser = Button("查看翻译网页");
    private readonly Label googleTestResult = Note("");
    private readonly ComboBox captureMode = new UiComboBox() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 230 };
    private readonly NumericUpDown cropX = Number(0, 99, 0, 1), cropY = Number(0, 99, 0, 1), cropW = Number(1, 100, 100, 1), cropH = Number(1, 100, 100, 1);
    private readonly NumericUpDown overlayWidth = Number(.3m, 2, .9m, .1m), overlayDistance = Number(.4m, 4, 1.2m, .1m), overlayY = Number(-2, 2, -.35m, .05m), duration = Number(0, 300, 20, 1);
    private readonly CheckBox controller = new() { Text = "启用 VR 手柄触发", AutoSize = true }, showSource = new() { Text = "叠层同时显示原文", AutoSize = true };
    private readonly CheckBox spatialSelection = new() { Name = "spatialSelection", Text = "双手柄框选并固定译文（替代长按双圈）", AutoSize = true };
    private readonly NumericUpDown selectionDistance = Number(.4m, 4, 1.2m, .1m);
    private readonly Label selectionHelp = Note("");
    private readonly Label workspaceInputHelp = new() { AutoSize = true, ForeColor = UiTheme.Muted };
    private readonly Label sidebarInputHelp = new() { AutoSize = true, ForeColor = UiTheme.Muted, MaximumSize = new Size(180, 0) };
    private readonly Button translateButton = Button("截屏并翻译"), recognizeButton = Button("仅截屏识别"), cancelButton = Button("取消");
    private readonly Button saveScreenshotButton = Button("保存当前截图");
    private readonly TabControl tabs = new UiPages() { Dock = DockStyle.Fill, Margin = Padding.Empty };
    private AppSettings settings;
    private Hotkeys? hotkeys;
    private CancellationTokenSource? currentRequest;
    private CancellationTokenSource? selectionCapture;
    private CancellationTokenSource? loginCheck;
    private CancellationTokenSource? apiVisionCheck;
    private CancellationTokenSource? googleTranslationCheck;
    private string apiKeyAddress = "";
    private List<Bitmap> pages = [];
    private int pageIndex;
    private long hideAt;
    private long captureNumber;
    private long activeGestureId, pagesGestureId, overlayGestureId;
    private long selectionGestureId, lastSelectionPaintAt;
    private SpatialAnchor? activeAnchor, pagesAnchor, overlayAnchor;
    private Bitmap? selectionFrame;
    private float selectionFrameAspect;
    private bool selectionActive, selectionFrameVisible;
    private long lastHoldPaintAt, lastHoldGestureId;
    private int lastHoldBucket = -1;
    private bool holdOverlayVisible, lastHoldCancelStage, captureInProgress;
    private bool busy, closing, overlayVisible, disposedServices, setupDialogOpen;
    private readonly bool demo;

    public MainForm(bool demo)
    {
        this.demo = demo;
        string? loadError = null;
        try { settings = demo ? new AppSettings() : SettingsStore.Load(); }
        catch (Exception ex) { settings = new AppSettings(); loadError = "配置加载失败：" + ex.Message; }
        Text = "无言势翻译器";
        Font = new Font("Microsoft YaHei UI", 10);
        BackColor = Background;
        ForeColor = Ink;
        MinimumSize = new Size(1120, 780);
        Size = new Size(1280, 900);
        StartPosition = FormStartPosition.CenterScreen;
        BuildUi();
        LoadSettings();
        cancelButton.Enabled = false;
        saveScreenshotButton.Enabled = false;
        saveScreenshotButton.Click += (_, _) => TryAction(SaveCurrentScreenshot);
        translateButton.Click += async (_, _) => await CaptureAndTranslate(false);
        recognizeButton.Click += async (_, _) => await CaptureAndTranslate(true);
        cancelButton.Click += (_, _) => CancelAllWork();
        vr.TranslationGestureStarted += id => { if (settings.ControllerEnabled) _ = CaptureAndTranslate(false, "VR 手柄长按", id); };
        vr.TranslationGestureCancelled += id => { if (settings.ControllerEnabled) CancelGesture(id); };
        vr.SelectionStarted += BeginSelection;
        vr.SelectionCommitted += selection => _ = CommitSelectionAsync(selection);
        vr.SelectionAborted += AbortSelection;
        vr.SpatialAnchorsInvalidated += InvalidateSpatialAnchors;
        vr.NextPagePressed += () => { if (settings.ControllerEnabled) ChangePage(1); };
        pollTimer.Tick += (_, _) => Poll();
        tray.DoubleClick += (_, _) => RestoreWindow();
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开设置与翻译结果", null, (_, _) => RestoreWindow());
        menu.Items.Add("截屏并翻译", null, async (_, _) => await CaptureAndTranslate(false, "托盘菜单"));
        menu.Items.Add("退出", null, (_, _) => Close());
        tray.ContextMenuStrip = menu;
        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized) { tray.Visible = true; Hide(); } };
        Shown += (_, _) =>
        {
            hotkeys = new Hotkeys(Handle);
            var shortcutErrors = new List<string>();
            try
            {
                hotkeys.Register(Hotkeys.Translate, settings.Hotkey);
            }
            catch (Exception ex)
            {
                shortcutErrors.Add(ex.Message);
                foreach (var fallback in new[] { "Ctrl+Alt+F8", "Ctrl+Alt+F9", "F8" })
                {
                    try
                    {
                        hotkeys.Register(Hotkeys.Translate, fallback);
                        settings.Hotkey = hotkeyText.Text = fallback;
                        shortcutErrors.Clear();
                        shortcutErrors.Add("原快捷键不可用，当前改用 " + fallback + "。可在设置中保存或修改。");
                        break;
                    }
                    catch (InvalidOperationException) { }
                }
            }
            foreach (var binding in new[] { (Hotkeys.Next, "Ctrl+Alt+Right"), (Hotkeys.Previous, "Ctrl+Alt+Left"), (Hotkeys.Toggle, "Ctrl+Alt+H") })
                try { hotkeys.Register(binding.Item1, binding.Item2); }
                catch (Exception ex) { shortcutErrors.Add(ex.Message); }
            if (shortcutErrors.Count > 0) SetStatus(string.Join(" ", shortcutErrors));
            if (!demo) ConnectVr();
            pollTimer.Start();
            if (demo) ShowDemo();
            else if (loadError != null) SetStatus(loadError);
            else if (status.Text.Length == 0) SetStatus(ReadyMessage());
        };
        FormClosing += OnClosing;
    }

    private void LoadSettings()
    {
        backend.SelectedIndex = settings.TranslationBackend switch { "OpenAI" => 1, "GoogleTranslate" => 2, "Credits" => 3, _ => 0 };
        creditServer.Text = settings.CreditServerUrl;
        processingMode.SelectedIndex = settings.ProcessingMode switch { "Image" => 1, "Handwriting" => 2, "LocalJapanese" => 3, _ => 0 };
        codexPath.Text = settings.CodexExecutablePath; codexModel.Text = settings.CodexModel;
        codexReasoningEffort.SelectedIndex = settings.CodexReasoningEffort switch { "low" => 1, "medium" => 2, "high" => 3, _ => 0 };
        endpoint.Text = settings.ApiBaseUrl; model.Text = settings.ApiModel; apiKey.Text = settings.ApiKey;
        apiImageDetail.SelectedIndex = settings.ApiImageDetail switch { "High" => 1, "Low" => 2, "Auto" => 3, _ => 0 };
        apiStream.Checked = settings.ApiStream;
        target.Text = settings.TargetLanguage; languages.Text = settings.OcrLanguages; hotkeyText.Text = settings.Hotkey;
        captureMode.SelectedItem = settings.CaptureMode; if (captureMode.SelectedIndex < 0) captureMode.SelectedIndex = 0;
        Put(cropX, settings.CropX * 100); Put(cropY, settings.CropY * 100); Put(cropW, settings.CropWidth * 100); Put(cropH, settings.CropHeight * 100);
        Put(overlayWidth, settings.OverlayWidth); Put(overlayDistance, settings.OverlayDistance); Put(overlayY, settings.OverlayVerticalOffset); Put(duration, settings.DisplaySeconds);
        controller.Checked = settings.ControllerEnabled; showSource.Checked = settings.ShowSource;
        spatialSelection.Checked = settings.SpatialSelectionEnabled;
        Put(selectionDistance, settings.SelectionDistance);
        UpdateBackendControls();
        UpdateRecognitionAction();
        ConfigureVrSelection();
        UpdateSelectionHelp();
    }

    private void UpdateBackendControls()
    {
        bool useCodex = backend.SelectedIndex == 0;
        bool useApi = backend.SelectedIndex == 1;
        bool useGoogle = backend.SelectedIndex == 2;
        bool useCredits = backend.SelectedIndex == 3;
        bool testing = apiVisionCheck is not null || googleTranslationCheck is not null;
        codexSettingsCard.Visible = useCodex;
        apiSettingsCard.Visible = useApi;
        googleSettingsCard.Visible = useGoogle;
        creditSettingsCard.Visible = useCredits;
        creditServer.Enabled = creditAccountButton.Enabled = useCredits && !testing;
        backend.Enabled = processingMode.Enabled = !testing;
        codexPath.Enabled = codexModel.Enabled = codexReasoningEffort.Enabled = useCodex;
        checkCodexLogin.Enabled = useCodex && loginCheck is null;
        endpoint.Enabled = model.Enabled = apiKey.Enabled = useApi && !testing;
        apiPreset.Enabled = useApiPreset.Enabled = apiImageDetail.Enabled = apiStream.Enabled = useApi && !testing;
        checkApiVision.Enabled = useApi;
        checkApiVision.Text = apiVisionCheck is not null ? "取消图片测试" : "测试图片识别";
        checkGoogleTranslate.Enabled = useGoogle;
        showGoogleBrowser.Enabled = useGoogle && !testing;
        checkGoogleTranslate.Text = googleTranslationCheck is not null ? "取消翻译测试" : "测试谷歌翻译";
        target.Enabled = !testing;
        bool directImage = processingMode.SelectedIndex == 1;
        bool handwriting = processingMode.SelectedIndex == 2;
        bool localJapanese = processingMode.SelectedIndex == 3;
        localOcrSettingsCard.Visible = localJapanese;
        if (localJapanese) localOcrStatus.Text = LocalJapaneseOcrService.GetAvailabilityMessage();
        showSource.Enabled = !directImage;
        dataNote.Text = useCredits
            ? (directImage || handwriting
                ? "截图将发送给积分服务，由服务端识别中、日、英文字并翻译。服务使用的模型由运营者配置。"
                : "图片在本机识别，只有识别原文发送给积分服务进行翻译。")
                + "\n完整翻译成功扣 1 积分；无文字、失败或服务端完成前取消不扣。重复查询同一任务不会重复扣分。"
                + "\n服务端已完成后隐藏字幕不会退回积分；断线时可在「账号与积分」找回最近结果。"
                + "\n服务端保存订单、消费记录和近期原文/译文用于恢复。截图仅用于本次处理；AI 仅识别请使用自备服务或本地 OCR。"
            : useGoogle
            ? (directImage || handwriting
                ? "Google Translate 只接受文字，无法直接识图或运行 AI 手写 OCR。\n请将识别方式改为「本地 OCR」或「本地日语手写 OCR」，再保存。"
                : "截图在本机 OCR 识别，只有识别出的文字会发送给 Google Translate；图片、API 密钥和聊天历史不会发送。\n「仅识别」不联网翻译。Google Translate 自动判断源语言，译文显示在下方字幕。")
                + "\n程序在后台打开 Google 翻译网页，输入原文并读取译文，不抢游戏焦点。可点「查看翻译网页」检查。\n网页要求验证、拒绝请求或超时时停止；不会自动切换服务。"
            : localJapanese
            ? "日语手写 OCR：使用 NDLOCR-Lite 在本机 CPU 识别，图片不会发送给翻译服务。\n"
                + (useCodex ? "识别出的日文交给 Codex 翻译，计入账号用量。" : "识别出的日文交给上方 API 翻译；选择本地翻译模型可全程离线。")
                + "\n「仅识别日语（本地）」不调用翻译服务。识别失败时会提示，不会自动改用云端识图。"
                + "\n截图仅临时写入本机，识别后删除；程序不自动保存截图或翻译历史。"
            : handwriting
            ? (useCodex
                ? "手写体 OCR：截图发给 Codex 定位字迹，再放大局部识别原文，最后翻译；这三步计入账号用量。"
                : "手写体 OCR：所选视觉 API 先定位字迹，再结合整图和局部放大图识别，最后翻译。支持本机服务；模型需支持多张图片。")
                + "\n自动识别中、日、英混合内容；看不清的字标为 [无法辨认]，结果仍需核对。"
                + "\n手写识别优先保留原图细节，并在桌面显示识别原文供核对。"
                + "\n「仅识别手写（AI）」只做定位与原文识别，不进行翻译。可手动保存当前截图；程序不自动记录历史。"
            : directImage
            ? useCodex
                ? "裁剪后的截图会发送给已登录的 Codex 模型，计入账号用量；不经过本地 OCR。\n「仅识别原文（AI）」也会发送截图，只返回原文。截图临时文件在请求结束后删除；程序不保存翻译历史。"
                : "裁剪后的截图会发送到上方 API 地址，由视觉模型理解并翻译，不经过本地 OCR。\n「仅识别原文（AI）」也会发送截图，只返回原文。程序不自动保存截图或翻译历史；模型需支持图片输入。"
            : useCodex
            ? "截图在本机识别；OCR 文字通过已登录的 Codex 账号翻译，计入账号用量。\n每次为独立临时翻译，不读取既有聊天历史。程序不自动保存截图或译文。"
            : "截图在本机识别；只有 OCR 文字会发送到上方 API 地址。\n程序不自动保存截图或翻译历史。需要可用的 API 账号或本地服务。";
        dataNote.Text += "\n以上选项需保存后生效；AI 设置有未保存改动时，暂停发送请求。";
    }

    internal bool HasPendingAiSettings =>
        settings.ProcessingMode != SelectedProcessingMode
        || settings.TranslationBackend != SelectedBackend
        || settings.CreditServerUrl != creditServer.Text.Trim()
        || settings.CodexExecutablePath != codexPath.Text.Trim() || settings.CodexModel != codexModel.Text.Trim()
        || settings.CodexReasoningEffort != SelectedCodexReasoningEffort
        || settings.ApiBaseUrl != endpoint.Text.Trim() || settings.ApiModel != model.Text.Trim()
        || settings.ApiImageDetail != SelectedApiImageDetail || settings.ApiStream != apiStream.Checked
        || settings.ApiKey != apiKey.Text.Trim() || settings.TargetLanguage != target.Text.Trim();

    private string ReadyMessage() => settings.TranslationBackend == "Credits"
        ? "就绪 · 积分服务，完整翻译成功扣 1 积分；请先在「AI 服务 → 账号与积分」登录。"
        : settings.TranslationBackend == "GoogleTranslate"
        ? "就绪 · 本地 OCR → Google Translate 后台网页，按 " + settings.Hotkey + " 翻译；可在「AI 服务」测试连接。"
        : settings.ProcessingMode == "LocalJapanese"
        ? "就绪 · 本地日语手写 OCR，按 " + settings.Hotkey + " 识别并翻译；「仅识别日语（本地）」可离线使用。"
        : settings.TranslationBackend == "Codex"
        ? "就绪 · 使用本机 Codex 登录，按 " + settings.Hotkey + " 翻译；可在「AI 服务」检测登录。"
        : "就绪 · 使用其他 AI API，按 " + settings.Hotkey + " 翻译；可在「AI 服务」测试图片识别。";

    private string SelectedApiImageDetail => apiImageDetail.SelectedIndex switch { 1 => "High", 2 => "Low", 3 => "Auto", _ => "Default" };
    private string SelectedCodexReasoningEffort => codexReasoningEffort.SelectedIndex switch { 1 => "low", 2 => "medium", 3 => "high", _ => "" };
    private string SelectedBackend => backend.SelectedIndex switch { 1 => "OpenAI", 2 => "GoogleTranslate", 3 => "Credits", _ => "Codex" };

    private string SelectedProcessingMode => processingMode.SelectedIndex switch { 1 => "Image", 2 => "Handwriting", 3 => "LocalJapanese", _ => "Ocr" };

    private void UpdateRecognitionAction()
    {
        // The workspace uses saved settings; a draft must not silently change where a screenshot goes.
        recognizeButton.Text = settings.ProcessingMode switch
        {
            "Image" => "仅识别原文（AI）",
            "Handwriting" => "仅识别手写（AI）",
            "LocalJapanese" => "仅识别日语（本地）",
            _ => "仅截屏识别"
        };
    }

    private void InvalidateApiTestResult()
    {
        if (apiVisionCheck is null && apiTestResult.Text.Length > 0)
            apiTestResult.Text = "API 配置已变更，请重新测试图片识别。";
    }

    private void ApplyApiPreset()
    {
        if (apiPreset.SelectedIndex < 0 || apiVisionCheck is not null || googleTranslationCheck is not null) return;
        var preset = ApiProviderPresets.All[apiPreset.SelectedIndex];
        if (preset.BaseUrl.Length == 0) { SetStatus("自定义地址已保留。填写模型和密钥后，可先测试图片识别。"); return; }
        if (ApiProviderPresets.ChangesAuthority(endpoint.Text, preset.BaseUrl))
        {
            apiKey.Clear(); model.Clear();
        }
        backend.SelectedIndex = 1;
        endpoint.Text = preset.BaseUrl;
        apiImageDetail.SelectedIndex = 0;
        apiStream.Checked = false;
        apiTestResult.Text = "";
        SetStatus($"已填入 {preset.Name} 地址。请填写此服务的视觉模型和密钥，测试后点击「保存设置」。");
    }

    private async Task CheckApiVision()
    {
        if (closing || apiVisionCheck is not null || googleTranslationCheck is not null) return;
        if (busy || selectionActive || selectionCapture is not null) { SetStatus("请等待当前翻译或框选完成，或先取消，再测试 API 图片识别。"); return; }
        var candidate = new AppSettings
        {
            TranslationBackend = "OpenAI", ProcessingMode = "Image",
            ApiBaseUrl = endpoint.Text.Trim(), ApiModel = model.Text.Trim(), ApiKey = apiKey.Text.Trim(),
            TargetLanguage = target.Text.Trim(), ApiImageDetail = SelectedApiImageDetail, ApiStream = apiStream.Checked
        };
        using var cancellation = new CancellationTokenSource();
        apiVisionCheck = cancellation;
        apiTestResult.Text = "正在用内置图片测试当前地址和模型…";
        UpdateBackendControls();
        try
        {
            byte[] png = ApiVisionProbe.CreateImage();
            string result = await translator.AnalyzeImageAsync(png, candidate, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!closing) apiTestResult.Text = ApiVisionProbe.DescribeResult(result);
        }
        catch (Exception ex)
        {
            if (!closing) apiTestResult.Text = cancellation.IsCancellationRequested ? "图片测试已取消。" : ex.Message;
        }
        finally { apiVisionCheck = null; if (!closing) UpdateBackendControls(); }
    }

    private async Task CheckGoogleTranslate()
    {
        if (closing || googleTranslationCheck is not null) return;
        if (busy || selectionActive || selectionCapture is not null || apiVisionCheck is not null) { SetStatus("请等待当前任务或框选完成或取消，再测试谷歌翻译。"); return; }
        var candidate = new AppSettings { TranslationBackend = "GoogleTranslate", TargetLanguage = target.Text.Trim() };
        using var cancellation = new CancellationTokenSource();
        googleTranslationCheck = cancellation;
        googleTestResult.Text = "正在后台网页输入固定文字 Hello world. 并等待读取译文…";
        UpdateBackendControls();
        try
        {
            string result = await translator.TranslateAsync("Hello world.", candidate, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!closing) googleTestResult.Text = "谷歌网页翻译成功：" + result;
        }
        catch (Exception ex)
        {
            if (!closing) googleTestResult.Text = cancellation.IsCancellationRequested ? "翻译测试已取消。" : ex.Message;
        }
        finally { googleTranslationCheck = null; if (!closing) UpdateBackendControls(); }
    }

    private async Task ShowGoogleBrowser()
    {
        if (closing) return;
        if (busy || selectionActive || selectionCapture is not null || apiVisionCheck is not null || googleTranslationCheck is not null)
        {
            SetStatus("请等待当前任务完成或取消，再查看翻译网页。");
            return;
        }
        try { await translator.ShowGoogleBrowserAsync(); }
        catch (Exception error) { if (!closing) googleTestResult.Text = error.Message; }
    }

    private async Task CheckCodexLogin()
    {
        if (loginCheck is not null || closing) return;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        loginCheck = cancellation;
        UpdateBackendControls();
        SetStatus("正在检测本机 Codex 程序和登录状态…");
        try
        {
            var candidate = new AppSettings { CodexExecutablePath = codexPath.Text.Trim(), CodexModel = codexModel.Text.Trim(), CodexReasoningEffort = SelectedCodexReasoningEffort };
            string result = await CodexTranslationClient.CheckLoginAsync(candidate, cancellation.Token);
            if (!closing) SetStatus(result);
        }
        catch (OperationCanceledException) { if (!closing) SetStatus("Codex 登录检测超时，请检查程序路径后重试。"); }
        catch (Exception ex) { if (!closing) SetStatus(ex.Message); }
        finally { loginCheck = null; if (!closing) UpdateBackendControls(); }
    }

    internal AppSettings ReadSettingsDraft() => new()
        {
            TranslationBackend = SelectedBackend,
            CreditServerUrl = creditServer.Text.Trim(),
            ProcessingMode = SelectedProcessingMode,
            CodexExecutablePath = codexPath.Text.Trim(), CodexModel = codexModel.Text.Trim(),
            CodexReasoningEffort = SelectedCodexReasoningEffort,
            ApiBaseUrl = endpoint.Text.Trim(), ApiModel = model.Text.Trim(), ApiKey = apiKey.Text.Trim(),
            ApiImageDetail = SelectedApiImageDetail, ApiStream = apiStream.Checked,
            TargetLanguage = target.Text.Trim(), OcrLanguages = languages.Text.Trim(), Hotkey = hotkeyText.Text.Trim(),
            CaptureMode = captureMode.Text, CropX = (float)cropX.Value / 100, CropY = (float)cropY.Value / 100,
            CropWidth = (float)cropW.Value / 100, CropHeight = (float)cropH.Value / 100,
            OverlayWidth = (float)overlayWidth.Value, OverlayDistance = (float)overlayDistance.Value,
            OverlayVerticalOffset = (float)overlayY.Value, DisplaySeconds = (int)duration.Value,
            ControllerEnabled = controller.Checked, ShowSource = showSource.Checked,
            SpatialSelectionEnabled = spatialSelection.Checked, SelectionDistance = (float)selectionDistance.Value
        };

    private void ApplySettings() => SaveSettings(ReadSettingsDraft());

    private void SaveSettings(AppSettings changed)
    {
        if (busy) throw new InvalidOperationException("请等待本次翻译完成或先取消，再保存设置。");
        if (apiVisionCheck is not null) throw new InvalidOperationException("请等待图片测试完成或取消，再保存设置。");
        if (googleTranslationCheck is not null) throw new InvalidOperationException("请等待谷歌翻译测试完成或取消，再保存设置。");
        SettingsValidation.ValidateForSave(changed);
        try
        {
            hotkeys?.Register(Hotkeys.Translate, changed.Hotkey);
            SettingsStore.Save(changed);
        }
        catch
        {
            try { hotkeys?.Register(Hotkeys.Translate, settings.Hotkey); } catch { }
            throw;
        }
        settings = changed;
        string? runtimeWarning = null;
        try
        {
            CancelSelectionCapture();
            vr.SelectionEnabled = false;
            ConfigureVrSelection();
        }
        catch (Exception error)
        {
            // Persistence already succeeded. A disconnected headset must not make
            // the wizard claim that saving failed or let Cancel imply a rollback.
            RuntimeFaultLog.Record("settings.apply-vr", error);
            runtimeWarning = " SteamVR 暂未应用新设置，请重新连接。";
        }
        UpdateSelectionHelp();
        UpdateRecognitionAction();
        SetStatus("设置已保存 · " + ReadyMessage() + runtimeWarning);
    }

    private void OpenSetupWizard()
    {
        if (closing || setupDialogOpen) return;
        if (busy || selectionActive || selectionCapture is not null || loginCheck is not null
            || apiVisionCheck is not null || googleTranslationCheck is not null)
            throw new InvalidOperationException("请先完成或取消当前任务，再打开配置向导。");
        bool resumePolling = pollTimer.Enabled;
        setupDialogOpen = true;
        pollTimer.Stop();
        try
        {
            vr.ResetTranslationInput();
            TryAction(HideHoldOverlay);
            using var setup = new SetupWizardForm(ReadSettingsDraft(), SaveSettings, firstRun: false);
            if (setup.ShowDialog(this) == DialogResult.OK) LoadSettings();
        }
        finally
        {
            // A button held while configuring must be released before the next capture.
            try { vr.ResetTranslationInput(); }
            finally
            {
                setupDialogOpen = false;
                if (!closing && resumePolling) pollTimer.Start();
            }
        }
    }

    private async Task CaptureAndTranslate(bool ocrOnly, string source = "桌面按钮", long gestureId = 0, SelectionCaptureSnapshot? selection = null)
    {
        if (closing || setupDialogOpen) return;
        if (ocrOnly && settings.TranslationBackend == "Credits" && settings.ProcessingMode is "Image" or "Handwriting")
        {
            SetStatus("积分服务提供完整识别与翻译，每次成功扣 1 分。仅识别可选择本地 OCR、本地日语 OCR 或自备 AI 服务。");
            return;
        }
        if (apiVisionCheck is not null) { SetStatus("API 图片测试进行中，请等待完成或先取消测试。"); return; }
        if (googleTranslationCheck is not null) { SetStatus("谷歌翻译测试进行中，请等待完成或先取消测试。"); return; }
        if ((settings.ProcessingMode != SelectedProcessingMode || !ocrOnly || settings.ProcessingMode is "Handwriting" or "Image") && HasPendingAiSettings)
        {
            SetStatus("AI 设置有未保存改动。请先在「AI 服务」点击「保存设置」，再截屏翻译。");
            ShowStatus("请先保存 AI 设置");
            return;
        }
        if (selection is null) CancelSelectionCapture();
        else if (!vr.IsAnchorValid(selection.Anchor)) { SetStatus("框选位置已失效，请重新拉框。"); return; }
        if (!captureRequests.Enqueue(new CaptureRequest(ocrOnly, source, gestureId, selection)))
        {
            RequestCancellation(currentRequest);
            SetStatus("已收到新的截屏请求，正在停止上一轮并获取新画面…");
            return;
        }
        busy = true;
        translateButton.Text = "重新截屏并翻译";
        cancelButton.Enabled = true;
        try
        {
            // One owner drains only the latest request. A new hold cancels the old AI task;
            // no two requests race to replace the preview or publish a stale response.
            while (!closing && captureRequests.TryTake(out var request))
                await RunCaptureAsync(request);
        }
        finally
        {
            captureRequests.Finish(); busy = false;
            if (!closing) { translateButton.Text = "截屏并翻译"; UpdateCancelButton(); }
            else { DisposeServices(); Close(); }
        }
    }

    private async Task RunCaptureAsync(CaptureRequest request)
    {
        bool ocrOnly = request.OcrOnly;
        activeGestureId = request.GestureId;
        activeAnchor = request.Selection?.Anchor;
        currentRequest = new CancellationTokenSource();
        var token = currentRequest.Token;
        var started = Stopwatch.StartNew();
        long number = ++captureNumber;
        bool captured = false;
        try
        {
            ValidateBackendMode(settings);
            SetStatus($"正在截取第 {number} 张 · {request.Source}…");
            imageBox.Text = $"正在获取截图 #{number} · {request.Source} · 预览暂为上一张";
            sourceText.Clear(); translatedText.Clear();
            ClearPages();
            Bitmap croppedImage;
            if (request.Selection is { } snapshot)
            {
                if (!vr.IsAnchorValid(snapshot.Anchor)) throw new InvalidOperationException("框选位置已失效，请重新拉框。");
                croppedImage = snapshot.DecodeImage();
            }
            else
            {
                if (settings.CaptureMode == "VR 左眼" && !vr.IsConnected && !vr.Connect()) throw new InvalidOperationException(vr.Status);
                // Neither subtitles, progress nor a selection frame should appear in a capture.
                captureInProgress = true;
                HideOverlay(); HideHoldOverlay(); HideSelectionFrame();
                bool restoreDesktopWindow = settings.CaptureMode != "VR 左眼" && Visible;
                Bitmap image;
                if (restoreDesktopWindow) Hide();
                try
                {
                    if (settings.CaptureMode == "VR 左眼") vr.PrepareEyeCapture();
                    await Task.Delay(80, token);
                    image = settings.CaptureMode == "VR 左眼" ? vr.CaptureEye() : CaptureService.CaptureDesktop();
                }
                finally
                {
                    captureInProgress = false;
                    if (restoreDesktopWindow && !closing) Show();
                }
                using (image) croppedImage = CaptureService.Crop(image, settings.CropX, settings.CropY, settings.CropWidth, settings.CropHeight);
            }
            using var cropped = croppedImage;
            token.ThrowIfCancellationRequested();
            var old = preview.Image; preview.Image = new Bitmap(cropped); old?.Dispose();
            saveScreenshotButton.Enabled = true;
            captured = true;
            imageBox.Text = $"截图 #{number} · {DateTime.Now:HH:mm:ss.fff} · {request.Source} · {cropped.Width} × {cropped.Height}";
            imageBox.Refresh();
            if (!ocrOnly && settings.TranslationBackend == "Credits" && settings.ProcessingMode is "Image" or "Handwriting")
            {
                byte[] png = await Task.Run(() => VisionImage.PrepareForRecognition(cropped), token);
                await RunCreditTranslationAsync(request, started, null, png, token);
                return;
            }
            if (!ocrOnly && settings.ProcessingMode == "Image")
            {
                sourceText.Text = "直接识图模式：将本次截图发送给模型，未运行本地 OCR。\r\n模型会自行组织识别与翻译结果。";
                SetStatus(settings.TranslationBackend == "Codex" ? "正在将截图交给 Codex 直接识图…" : "正在将截图交给兼容 API 的视觉模型…");
                ShowStatus("正在直接识图…");
                byte[] png = await Task.Run(() => VisionImage.Prepare(cropped), token);
                var result = await translator.AnalyzeImageAsync(png, settings, token);
                token.ThrowIfCancellationRequested();
                translatedText.Text = result;
                await PublishResultPagesAsync(result, request.GestureId, token, title: "识图结果");
                SetStatus($"识图完成 · {started.Elapsed.TotalSeconds:F1} 秒 · {pages.Count} 页 · 快捷键 {settings.Hotkey}");
                return;
            }
            bool localJapanese = settings.ProcessingMode == "LocalJapanese";
            bool handwriting = settings.ProcessingMode is "Handwriting" or "LocalJapanese";
            bool aiTranscription = settings.ProcessingMode == "Handwriting" || (ocrOnly && settings.ProcessingMode == "Image");
            string text;
            if (localJapanese)
            {
                SetStatus("正在本机 CPU 识别日语手写…首次使用需要加载模型。");
                ShowStatus("正在本地识别日语…");
                byte[] png = await Task.Run(() => VisionImage.Prepare(cropped), token);
                text = await localJapaneseOcr.RecognizeAsync(png, token);
            }
            else if (aiTranscription)
            {
                SetStatus(settings.TranslationBackend == "Codex" ? "正在通过 Codex 定位字迹并放大识别…" : "正在通过所选视觉 API 定位字迹并放大识别…");
                ShowStatus(handwriting ? "正在识别手写原文…" : "正在识别原文…");
                byte[] png = await Task.Run(() => VisionImage.PrepareForRecognition(cropped), token);
                text = await translator.RecognizeHandwritingAsync(png, settings, token);
            }
            else
            {
                SetStatus("正在本机识别文字…"); ShowStatus("正在识别文字…");
                text = await Task.Run(() => ocr.Recognize(cropped, settings.OcrLanguages), token);
            }
            token.ThrowIfCancellationRequested();
            sourceText.Text = text;
            if (string.IsNullOrWhiteSpace(text))
            {
                SetStatus(localJapanese
                    ? "没有识别到日语文字。请靠近字迹、缩小识别范围，并保持文字清晰。"
                    : aiTranscription
                    ? "没有识别到可读的手写文字。请靠近字迹、缩小识别范围，并检查模型是否支持图像输入。"
                    : "没有识别到文字。请靠近文字、缩小识别范围，或检查 OCR 语言。");
                ShowStatus("没有识别到文字"); return;
            }
            if (ocrOnly)
            {
                SetStatus($"{(handwriting ? "手写原文识别" : aiTranscription ? "AI 原文识别" : "本地识别")}完成 · {text.Length} 字符 · {started.Elapsed.TotalSeconds:F1} 秒");
                await PublishResultPagesAsync(text, request.GestureId, token, title: handwriting ? "手写原文" : "识别原文"); return;
            }
            if (settings.TranslationBackend == "Credits")
            {
                await RunCreditTranslationAsync(request, started, text, null, token);
                return;
            }
            if (settings.TranslationBackend == "OpenAI" && string.IsNullOrWhiteSpace(settings.ApiModel))
            {
                SetStatus("截图和识别已完成。请在「AI 服务」填写模型、地址及密钥，再开始翻译。");
                ShowStatus("识别完成 · 请先配置 AI 服务"); return;
            }
            SetStatus(settings.TranslationBackend switch
            {
                "Codex" => "文字已识别，正在通过本机 Codex 翻译…",
                "GoogleTranslate" => "文字已识别，正在后台 Google 翻译网页输入并等待译文…",
                _ => "文字已识别，正在请求兼容 API 翻译…"
            }); ShowStatus("正在翻译…");
            var translation = await translator.TranslateAsync(text, settings, token);
            token.ThrowIfCancellationRequested();
            translatedText.Text = translation;
            await PublishResultPagesAsync(translation, request.GestureId, token, settings.ShowSource ? text : null);
            SetStatus($"翻译完成 · {started.Elapsed.TotalSeconds:F1} 秒 · {pages.Count} 页 · 快捷键 {settings.Hotkey}");
        }
        catch (Exception ex) { ReportCaptureFailure(ex, token); }
        finally
        {
            currentRequest.Dispose(); currentRequest = null;
            activeGestureId = 0;
            activeAnchor = null;
            captureInProgress = false;
            if (!captured) imageBox.Text = $"截图 #{number} 未更新 · 预览仍为上一张";
        }
    }

    private void ConfigureVrSelection()
    {
        vr.SelectionDistance = settings.SelectionDistance;
        vr.SelectionMode = settings.SpatialSelectionEnabled;
        vr.SelectionEnabled = settings.ControllerEnabled && !closing;
    }

    private void UpdateSelectionHelp()
    {
        selectionDistance.Enabled = spatialSelection.Checked;
        selectionHelp.Text = spatialSelection.Checked
            ? "按住左 X，用双手柄分别指向矩形的两个对角；松开 X 提交。\n仅识别框内，译文留在框的位置，翻页不移动。框选距离为前方平面距离。\n固定在 SteamVR 追踪空间；游戏内传送、移动后不会跟随原物体。桌面按钮和快捷键仍使用普通截图。"
            : "1.5 秒：第一圈完成，截屏并翻译。\n3 秒：第二圈完成，取消本次并隐藏字幕。松开后可再次触发。";
        workspaceInputHelp.Text = settings.SpatialSelectionEnabled
            ? "左手柄 X     按住，用双手柄指向矩形对角 → 松开提交\n译文固定在选区 · 新框替换旧译文 · 快捷键仍为普通截图"
            : "左手柄 X     按住 1.5 秒 → 截屏翻译；继续按到 3 秒 → 取消本次\n松开后可再次触发 · 左摇杆 / 菜单键翻页";
        sidebarInputHelp.Text = settings.SpatialSelectionEnabled ? "左 X · 按住双手柄拉框\n松开翻译，固定原位" : "左 X  ·  长按 1.5 秒翻译";
    }

    private void UpdateCancelButton() => cancelButton.Enabled = busy || selectionActive || selectionCapture is not null || pagesAnchor is not null;

    private void ClearPages()
    {
        foreach (var page in pages) page.Dispose();
        pages.Clear(); pagesGestureId = 0; pagesAnchor = null;
        UpdateCancelButton();
    }

    private void CancelSelectionCapture()
    {
        RequestCancellation(selectionCapture);
        selectionCapture = null;
        selectionActive = false;
        selectionGestureId = 0;
        TryAction(HideSelectionFrame);
        UpdateCancelButton();
    }

    private void CancelAllWork()
    {
        captureRequests.CancelPending(); RequestCancellation(currentRequest);
        CancelSelectionCapture(); ClearPages();
        TryAction(HideOverlay); TryAction(HideHoldOverlay);
        SetStatus("已取消，字幕已收起。");
    }

    private void BeginSelection(long gestureId)
    {
        if (closing || setupDialogOpen || !settings.SpatialSelectionEnabled || !settings.ControllerEnabled) return;
        if (apiVisionCheck is not null || googleTranslationCheck is not null || HasPendingAiSettings)
        {
            SetStatus("请先完成服务测试或保存 AI 设置，再开始框选。");
            return;
        }
        CancelSelectionCapture();
        selectionGestureId = gestureId; selectionActive = true;
        captureRequests.CancelPending(); RequestCancellation(currentRequest);
        ClearPages();
        TryAction(HideOverlay); TryAction(HideHoldOverlay);
        SetStatus("正在框选：双手柄指向矩形两个对角，松开左 X 提交。");
        UpdateCancelButton();
    }

    private async Task CommitSelectionAsync(SpatialSelection selection)
    {
        if (closing || !selectionActive || selectionGestureId != selection.GestureId) return;
        selectionActive = false;
        using var operation = new CancellationTokenSource();
        selectionCapture = operation;
        CancellationToken token = operation.Token;
        try
        {
            HideOverlay(); HideHoldOverlay(); HideSelectionFrame();
            SetStatus("框选完成，正在获取框内画面…");
            vr.PrepareEyeCapture();
            await Task.Delay(80, token);
            token.ThrowIfCancellationRequested();
            if (!vr.IsAnchorValid(selection.Anchor)) throw new InvalidOperationException("框选位置已失效，请重新拉框。");
            using var image = vr.CaptureSelection(selection.Anchor);
            byte[] png = await Task.Run(() => VisionImage.PrepareForRecognition(image), token);
            token.ThrowIfCancellationRequested();
            if (closing || !ReferenceEquals(selectionCapture, operation) || selectionGestureId != selection.GestureId) return;
            var snapshot = new SelectionCaptureSnapshot(png, selection.Anchor);
            selectionCapture = null; selectionGestureId = 0;
            UpdateCancelButton();
            await CaptureAndTranslate(false, "双手柄框选", selection.GestureId, snapshot);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!closing && !token.IsCancellationRequested && ReferenceEquals(selectionCapture, operation))
                SetStatus("框选截图未完成：" + error.Message);
        }
        finally
        {
            if (ReferenceEquals(selectionCapture, operation))
            {
                selectionCapture = null; selectionGestureId = 0;
                TryAction(HideSelectionFrame);
            }
            if (!closing) UpdateCancelButton();
            else if (!busy && !IsDisposed) { DisposeServices(); Close(); }
        }
    }

    private void AbortSelection(string reason)
    {
        if (selectionActive || selectionCapture is not null) CancelSelectionCapture();
        if (!closing) SetStatus("框选已取消：" + reason);
    }

    internal void InvalidateSpatialAnchors()
    {
        bool hadSelection = selectionActive || selectionCapture is not null;
        bool pending = captureRequests.CancelSpatialPending();
        bool active = activeAnchor is not null;
        bool displayed = pagesAnchor is not null || overlayAnchor is not null;
        if (active) RequestCancellation(currentRequest);
        CancelSelectionCapture();
        if (pagesAnchor is not null) { ClearPages(); translatedText.Clear(); }
        if (overlayAnchor is not null) TryAction(HideOverlay);
        if (!closing && (hadSelection || pending || active || displayed)) SetStatus("SteamVR 追踪空间已变化，固定选区已清除，请重新拉框。");
    }

    private void UpdateSelectionFrame()
    {
        if (closing || !settings.SpatialSelectionEnabled || !settings.ControllerEnabled || !selectionActive
            || selectionCapture is not null || captureInProgress || !vr.IsConnected
            || vr.CurrentSelection is not { } selection || selection.GestureId != selectionGestureId
            || !vr.IsAnchorValid(selection.Anchor))
        {
            if (selectionFrameVisible) HideSelectionFrame();
            return;
        }
        long now = Environment.TickCount64;
        if (selectionFrameVisible && now - lastSelectionPaintAt < 33) return;
        // Quantized aspect avoids allocating a new bitmap for sub-pixel controller jitter.
        float aspect = Math.Clamp(MathF.Round(selection.Anchor.Aspect * 10) / 10, .2f, 5);
        if (selectionFrame is null || Math.Abs(aspect - selectionFrameAspect) > .001f)
        {
            var frame = SelectionOverlayRenderer.RenderFrame(aspect);
            selectionFrame?.Dispose(); selectionFrame = frame; selectionFrameAspect = aspect;
        }
        vr.ShowSelectionFrame(selectionFrame, selection.Anchor);
        selectionFrameVisible = true; lastSelectionPaintAt = now;
    }

    private void HideSelectionFrame()
    {
        vr.HideSelectionFrame(); selectionFrameVisible = false;
    }

    internal void ReportCaptureFailure(Exception error, CancellationToken token)
    {
        if (closing) return;
        // Some providers can fail with an ordinary exception concurrently with cancellation.
        // A cancelled gesture must not bring back an error subtitle after it was dismissed.
        if (token.IsCancellationRequested || error is OperationCanceledException)
        {
            if (!captureRequests.HasPending && !selectionActive && selectionCapture is null) { SetStatus("已取消。"); TryAction(HideOverlay); }
            return;
        }
        SetStatus(error.Message);
        ShowStatus("操作未完成 · 请查看桌面窗口");
    }

    internal void CancelGesture(long gestureId)
    {
        if (gestureId == 0) return;
        bool pending = captureRequests.CancelPending(gestureId);
        bool active = activeGestureId == gestureId;
        bool displayed = pagesGestureId == gestureId || overlayGestureId == gestureId;
        if (active) RequestCancellation(currentRequest);
        if (pagesGestureId == gestureId)
        {
            ClearPages();
            translatedText.Clear();
        }
        if (overlayGestureId == gestureId) TryAction(HideOverlay);
        if (vr.HoldGestureId == gestureId) TryAction(HideHoldOverlay);
        if (pending || active || displayed) SetStatus("第二圈已满，本次翻译已取消，字幕已收起。松开后可重新长按。");
    }

    private void ConnectVr()
    {
        if (busy) { SetStatus("请等待本次翻译完成后再连接 SteamVR。"); return; }
        reconnectAt = 0; reconnectAttempts = 0; healthyInputSince = 0;
        CancelSelectionCapture();
        if (vr.IsConnected) vr.Disconnect();
        overlayVisible = holdOverlayVisible = false; hideAt = 0; lastHoldBucket = -1;
        vr.Connect(); ConfigureVrSelection(); vrStatus.Text = vr.Status;
    }

    private void Poll()
    {
        if (closing || setupDialogOpen) return;
        try { TryRecoverVrConnection(); }
        catch (Exception error)
        {
            RuntimeFaultLog.Record("reconnect", error);
            reconnectAt = reconnectAttempts >= 3 ? 0 : Environment.TickCount64 + 2000;
            SetStatus("SteamVR 恢复暂未完成：" + error.Message);
        }
        bool inputHealthy = VrFrameGuard.PollInput(() =>
        {
            vr.SelectionEnabled = settings.ControllerEnabled && apiVisionCheck is null && googleTranslationCheck is null;
            vr.Poll();
        }, error => ReportVrFault("input", error));
        if (!inputHealthy)
        {
            RequestCancellation(currentRequest);
            try { vr.Disconnect(); } catch (Exception error) { RuntimeFaultLog.Record("disconnect", error); }
            healthyInputSince = 0;
            reconnectAt = reconnectAttempts >= 3 ? 0 : Environment.TickCount64 + 2000;
        }
        else if (vr.IsConnected)
        {
            if (healthyInputSince == 0) healthyInputSince = Environment.TickCount64;
            if (Environment.TickCount64 - healthyInputSince >= 10000) reconnectAttempts = 0;
        }
        if (!vr.IsConnected) { overlayVisible = holdOverlayVisible = false; hideAt = 0; lastHoldBucket = -1; }
        long now = Environment.TickCount64;
        holdFrames.Draw(now, UpdateHoldOverlay, error => ReportVrFault("hold-overlay", error));
        selectionFrames.Draw(now, UpdateSelectionFrame, error => ReportVrFault("selection-overlay", error));
        expiryFrames.Draw(now, () => { if (hideAt > 0 && now >= hideAt) HideOverlay(); },
            error => ReportVrFault("overlay-expiry", error));
        if (vrStatus.Text != vr.Status) vrStatus.Text = vr.Status;
    }

    private void ReportVrFault(string operation, Exception error)
    {
        RuntimeFaultLog.Record(operation, error);
        SetStatus(error is VrOverlayException
            ? "叠层暂时未更新，手柄仍可操作；正在重试显示。" : "SteamVR：" + error.Message);
    }

    private void RequestCancellation(CancellationTokenSource? source) =>
        _ = BackgroundCancellation.RequestAsync(source, demo ? null : error => RuntimeFaultLog.Record("cancel-callback", error));

    private void TryRecoverVrConnection()
    {
        if (vr.IsConnected) { reconnectAt = 0; return; }
        if (reconnectAt == 0 || busy || Environment.TickCount64 < reconnectAt) return;
        reconnectAttempts++;
        // Only repair an unexpected failure while SteamVR is still running. Never relaunch it after the user exits.
        var servers = Process.GetProcessesByName("vrserver");
        bool running = servers.Length > 0;
        foreach (var server in servers) server.Dispose();
        if (!running) { reconnectAt = 0; return; }
        if (vr.Connect())
        {
            reconnectAt = 0; healthyInputSince = 0; ConfigureVrSelection();
            RuntimeFaultLog.Record("reconnected");
            SetStatus("SteamVR 已恢复连接，请先松开 X，再重新长按。");
        }
        else
        {
            reconnectAt = reconnectAttempts >= 3 ? 0 : Environment.TickCount64 + 2000 * reconnectAttempts;
            SetStatus(reconnectAt == 0 ? vr.Status : "正在恢复 SteamVR 连接…");
        }
    }

    private void UpdateHoldOverlay()
    {
        if (!closing && vr.IsConnected) vr.TryPrepareHoldProgressTexture();
        if (closing || captureInProgress || settings.SpatialSelectionEnabled || !settings.ControllerEnabled || !vr.IsConnected || !vr.HoldVisible)
        {
            if (holdOverlayVisible) HideHoldOverlay();
            return;
        }
        int bucket = HoldProgressAtlas.FrameIndex(vr.HoldProgress);
        bool sameStage = lastHoldCancelStage == vr.HoldCancelStage && lastHoldGestureId == vr.HoldGestureId;
        long now = Environment.TickCount64;
        if (holdOverlayVisible && sameStage && (bucket == lastHoldBucket || now - lastHoldPaintAt < 33)) return;
        if (!vr.ShowHoldProgress(vr.HoldProgress, vr.HoldCancelStage, settings.OverlayDistance, settings.OverlayVerticalOffset + .15f)) return;
        holdOverlayVisible = true; lastHoldBucket = bucket; lastHoldPaintAt = now;
        lastHoldCancelStage = vr.HoldCancelStage; lastHoldGestureId = vr.HoldGestureId;
    }

    private void HideHoldOverlay()
    {
        vr.HideHoldOverlay(); holdOverlayVisible = false; lastHoldBucket = -1;
    }

    private void ShowStatus(string message)
    {
        if (!vr.IsConnected || selectionActive || selectionCapture is not null) return;
        try
        {
            if (activeAnchor is { } anchor && !vr.IsAnchorValid(anchor)) return;
            using var bitmap = activeAnchor is { } fixedAnchor
                ? SelectionOverlayRenderer.RenderStatus(message, fixedAnchor.Aspect) : OverlayRenderer.RenderStatus(message);
            if (activeAnchor is { } spatial) vr.ShowSpatialOverlay(bitmap, spatial);
            else vr.ShowOverlay(bitmap, settings.OverlayWidth, settings.OverlayDistance, settings.OverlayVerticalOffset);
            overlayAnchor = activeAnchor;
            overlayGestureId = activeGestureId;
            overlayVisible = true; ResetExpiry();
        }
        catch (Exception ex) { SetStatus("叠层显示失败：" + ex.Message); }
    }

    private void SetPages(List<Bitmap> newPages, long gestureId = 0, SpatialAnchor? anchor = null)
    {
        if (anchor is { } spatial && !vr.IsAnchorValid(spatial))
        {
            foreach (var page in newPages) page.Dispose();
            throw new InvalidOperationException("框选位置已失效，请重新拉框。");
        }
        foreach (var page in pages) page.Dispose();
        pages = newPages; pagesGestureId = gestureId; pagesAnchor = anchor; pageIndex = 0; ShowPage(); UpdateCancelButton();
    }

    private void ChangePage(int offset)
    {
        if (setupDialogOpen || pages.Count == 0 || busy || selectionActive || selectionCapture is not null) return;
        pageIndex = (pageIndex + offset + pages.Count) % pages.Count;
        TryAction(() => { ShowPage(); SetStatus($"当前叠层第 {pageIndex + 1} / {pages.Count} 页。"); });
    }

    private void ShowPage()
    {
        if (pages.Count == 0 || !vr.IsConnected || selectionActive || selectionCapture is not null) return;
        if (pagesAnchor is { } anchor)
        {
            if (!vr.IsAnchorValid(anchor)) { ClearPages(); TryAction(HideOverlay); throw new InvalidOperationException("框选位置已失效，请重新拉框。"); }
            vr.ShowSpatialOverlay(pages[pageIndex], anchor);
        }
        else vr.ShowOverlay(pages[pageIndex], settings.OverlayWidth, settings.OverlayDistance, settings.OverlayVerticalOffset);
        overlayAnchor = pagesAnchor;
        overlayGestureId = pagesGestureId;
        overlayVisible = true; ResetExpiry();
    }

    private void ResetExpiry() => hideAt = overlayAnchor is not null || settings.DisplaySeconds == 0 ? 0 : Environment.TickCount64 + settings.DisplaySeconds * 1000L;
    private void HideOverlay() { vr.HideOverlay(); overlayVisible = false; overlayGestureId = 0; overlayAnchor = null; hideAt = 0; }

    private void ShowDemo()
    {
        if (busy || selectionActive || selectionCapture is not null) return;
        sourceText.Text = "Welcome to the world!\r\nPress the button to translate what you see.";
        translatedText.Text = "欢迎来到这个世界！\r\n按下按键，即可翻译眼前的文字。\r\n\r\n这是显示测试，没有调用 AI 服务。";
        TryAction(() => SetPages(OverlayRenderer.RenderPages(translatedText.Text)));
        SetStatus(vr.IsConnected ? "测试文字已发送到 VR 叠层。" : "桌面示例已显示。连接 SteamVR 后可测试头显中的叠层。");
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0312 && !closing && !setupDialogOpen)
        {
            switch (m.WParam.ToInt32())
            {
                case Hotkeys.Translate: _ = CaptureAndTranslate(false, "键盘快捷键"); break;
                case Hotkeys.Next: ChangePage(1); break;
                case Hotkeys.Previous: ChangePage(-1); break;
                case Hotkeys.Toggle: if (!busy) TryAction(() => { if (overlayVisible) HideOverlay(); else ShowPage(); }); break;
            }
        }
        base.WndProc(ref m);
    }

    protected override bool ShowWithoutActivation => demo;

    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        closing = true; RequestCancellation(loginCheck); RequestCancellation(apiVisionCheck); RequestCancellation(googleTranslationCheck); pollTimer.Stop(); hotkeys?.Dispose(); tray.Visible = false;
        bool capturingSelection = selectionCapture is not null;
        CancelSelectionCapture();
        captureRequests.CancelPending();
        if (busy || capturingSelection)
        {
            e.Cancel = true; RequestCancellation(currentRequest); Hide(); return;
        }
        DisposeServices();
    }

    private void DisposeServices()
    {
        if (disposedServices) return;
        disposedServices = true;
        pollTimer.Dispose(); tray.Dispose(); vr.Dispose(); ocr.Dispose(); localJapaneseOcr.Dispose(); translator.Dispose();
        selectionFrame?.Dispose(); selectionFrame = null;
        foreach (var page in pages) page.Dispose(); preview.Image?.Dispose();
    }

    private void RestoreWindow() { Show(); WindowState = FormWindowState.Normal; Activate(); }
    internal static void ValidateBackendMode(AppSettings settings) => SettingsValidation.ValidateBackendMode(settings);
    private void SetStatus(string text) => status.Text = text;
    private void TryAction(Action action) { try { action(); } catch (Exception ex) { SetStatus(ex.Message); } }
    private TabPage Page(string title) { var page = new TabPage(title) { BackColor = Background, ForeColor = Ink }; tabs.TabPages.Add(page); return page; }
    private static Button Button(string label) => new UiButton { Text = label, AutoSize = true, Margin = new Padding(0, 0, 8, 0) };
    private static TextBox Input() => new() { Width = 400, BackColor = UiTheme.Input, ForeColor = Ink, BorderStyle = BorderStyle.None };
    private static TextBox Readout() => new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BackColor = Surface, ForeColor = Ink, BorderStyle = BorderStyle.None, Font = new Font("Microsoft YaHei UI", 11) };
    private static Label Tiny(string text) => new() { Text = text, AutoSize = true, ForeColor = UiTheme.Muted, Margin = new Padding(0, 9, 8, 0) };
    private static Label Note(string text) => new() { Text = text, AutoSize = true, ForeColor = UiTheme.Muted, MaximumSize = new Size(730, 0), Margin = new Padding(0, 6, 0, 6), Font = new Font("Microsoft YaHei UI", 9) };
    private static FlowLayoutPanel Flow() => new() { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, WrapContents = true, Margin = Padding.Empty };
    private static NumericUpDown Number(decimal min, decimal max, decimal value, decimal increment) => new() { Minimum = min, Maximum = max, Value = value, Increment = increment, DecimalPlaces = increment < 1 ? 2 : 0, Width = 78, BackColor = UiTheme.Input, ForeColor = Ink, BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, 7, 18, 7) };
    private static void Put(NumericUpDown control, float value) => control.Value = float.IsFinite(value) ? Math.Clamp((decimal)value, control.Minimum, control.Maximum) : control.Minimum;
}
