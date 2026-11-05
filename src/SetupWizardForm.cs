namespace SteamVrTranslator;

/// <summary>A staged settings editor. Account access is explicit; opening or saving never calls a model.</summary>
internal sealed class SetupWizardForm : Form
{
    private readonly AppSettings initial;
    private readonly Action<AppSettings> saveSettings;
    private readonly bool firstRun;
    private readonly CreditSetupLoginControl login;
    private TableLayoutPanel loginPage = null!;
    private UiCard triggerCard = null!;
    private readonly Panel viewport = new() { Name = "wizardViewport", Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(0, 0, 8, 0) };
    private readonly Label errorLabel = Note("", "wizardError");
    private readonly Label pageTitle = Note("", "wizardPageTitle");
    private readonly Label modeNote = Note("", "wizardModeNote");
    private readonly Label providerNote = Note("", "wizardProviderNote");
    private readonly Label disclosure = Note("", "wizardDisclosure");
    private readonly Label triggerNote = Note("", "wizardTriggerNote");
    private readonly Label stepCount = Note("", "wizardProgress");
    private readonly TableLayoutPanel[] pages = [];
    private readonly UiButton nextButton = Button("下一步", "wizardNext", true);
    private readonly UiButton backButton = Button("返回", "wizardBack");
    private readonly UiButton cancelButton = Button("取消", "wizardCancel");
    private readonly SetupServiceChoice[] serviceChoices =
    [
        new() { Name = "wizardServiceCredits", Number = "01", Text = "账号积分服务（推荐）", Description = "登录一个账号即可开始 · 完整翻译成功扣 1 积分" },
        new() { Name = "wizardServiceApi", Number = "02", Text = "使用自己的 API 服务", Description = "填写地址、模型与密钥 · 支持云端和本机接口" },
        new() { Name = "wizardServiceCodex", Number = "03", Text = "连接本机 Codex", Description = "连接本机已登录的 Codex · 使用现有账号" },
        new() { Name = "wizardServiceGoogle", Number = "04", Text = "本地 OCR + Google 翻译", Description = "在电脑上识别文字，再交给 Google 翻译 · 无需 API 密钥" }
    ];
    private readonly ComboBox processingMode = Combo("wizardProcessingMode");
    private readonly TextBox targetLanguage = Input("wizardTargetLanguage");
    private readonly TextBox codexPath = Input("wizardCodexPath");
    private readonly TextBox codexModel = Input("wizardCodexModel");
    private readonly ComboBox codexReasoning = Combo("wizardCodexReasoningEffort");
    private readonly ComboBox apiPreset = Combo("wizardApiPreset");
    private readonly UiButton applyPresetButton = Button("使用地址", "wizardApplyApiPreset");
    private readonly TextBox apiEndpoint = Input("wizardApiEndpoint");
    private readonly TextBox apiModel = Input("wizardApiModel");
    private readonly TextBox apiKey = Input("wizardApiKey");
    private readonly ComboBox apiImageDetail = Combo("wizardApiImageDetail");
    private readonly CheckBox apiStream = Check("使用流式接口（部分模型需要）", "wizardApiStream");
    private readonly ComboBox captureMode = Combo("wizardCaptureMode");
    private readonly TextBox hotkey = Input("wizardHotkey");
    private readonly CheckBox controller = Check("启用 VR 手柄按键", "wizardControllerEnabled");
    private UiCard codexCard = null!, apiCard = null!, googleCard = null!;
    private readonly SetupServiceChoice desktop = new() { Name = "wizardDesktop", Number = "01", Text = "桌面", Description = "按快捷键截取主显示器，无需头显" };
    private readonly SetupServiceChoice headset = new() { Name = "wizardSteamVr", Number = "02", Text = "SteamVR", Description = "用手柄截取游戏画面，在视野下方看字幕" };
    private string credentialAddress = "";
    private bool loading = true, finishing;
    private int? modeBeforeGoogle;


    internal int CurrentStep { get; private set; }
    internal string LastError => errorLabel.Text;

    public SetupWizardForm(AppSettings initialSettings, Action<AppSettings> saveSettings, bool firstRun = true,
        Func<string, string, string, CancellationToken, Task<CreditSession>>? authenticate = null, bool startAtLogin = false)
    {
        ArgumentNullException.ThrowIfNull(initialSettings);
        ArgumentNullException.ThrowIfNull(saveSettings);
        initial = initialSettings.Copy();
        if (firstRun)
        {
            initial.TranslationBackend = "Credits";
            initial.CreditServerUrl = AppSettings.OfficialCreditServerUrl;
            initial.ProcessingMode = "Handwriting";
        }
        this.saveSettings = saveSettings;
        this.firstRun = firstRun;
        // The account destination is chosen outside the login form. Never send a saved password or token to a new host.
        bool validDestination = true;
        try { _ = CreditServiceClient.NormalizeServerUrl(initial.CreditServerUrl); }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException) { validDestination = false; }
        login = new CreditSetupLoginControl(validDestination ? initial.CreditServerUrl : AppSettings.OfficialCreditServerUrl, authenticate);
        login.Enabled = validDestination;
        login.RegistrationAvailable = initial.CreditServerUrl.TrimEnd('/').Equals(AppSettings.OfficialCreditServerUrl, StringComparison.OrdinalIgnoreCase);
        login.RegisterRequested += (_, _) => {
            if (!login.RegistrationAvailable) return;
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://mugonzei.top/account.html#register-tab") { UseShellExecute = true }); }
            catch { ShowError("无法打开浏览器。请在 mugonzei.top 的账户页注册后回来登录。"); }
        };
        login.SignedIn += (_, _) => { if (!IsDisposed && CurrentStep == 1 && SelectedBackend == "Credits") TryAdvance(); };

        Text = firstRun ? "无言势翻译器 · 初次设置" : "无言势翻译器 · 重新配置";
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Microsoft YaHei UI", 10f);
        BackColor = UiTheme.Background;
        ForeColor = UiTheme.Ink;
        MinimumSize = new Size(470, 510);
        Size = new Size(660, 640);
        StartPosition = firstRun ? FormStartPosition.CenterScreen : FormStartPosition.CenterParent;
        ShowInTaskbar = firstRun;
        MinimizeBox = false;
        Icon = UiTheme.AppIcon;
        DoubleBuffered = true;

        InitializeChoices();
        pages = [BuildChoicePage(), BuildServicePage(), BuildCapturePage()];
        BuildShell();
        WindowChrome.Apply(this);
        LoadInitialValues();
        loading = false;
        RefreshServiceChoices();
        ShowStep(startAtLogin && SelectedBackend == "Credits" ? 1 : 0);

        foreach (var choice in serviceChoices)
            choice.CheckedChanged += (_, _) => { if (!loading && choice.Checked) ServiceChanged(); };
        processingMode.SelectedIndexChanged += (_, _) => { if (!loading) RefreshServiceChoices(); };
        apiPreset.SelectedIndexChanged += (_, _) =>
        {
            if (apiPreset.SelectedIndex >= 0) providerNote.Text = ApiProviderPresets.All[apiPreset.SelectedIndex].Note;
        };
        apiEndpoint.TextChanged += (_, _) =>
        {
            if (loading) return;
            if (ApiProviderPresets.ChangesAuthority(credentialAddress, apiEndpoint.Text))
            {
                bool cleared = apiKey.TextLength > 0;
                apiKey.Clear(); apiModel.Clear();
                if (cleared) providerNote.Text = "服务地址已改变，旧密钥已清除。请填写新服务的模型和密钥；本机服务未启用鉴权时可留空。";
            }
            credentialAddress = apiEndpoint.Text;
        };
        apiKey.TextChanged += (_, _) => { if (!loading) credentialAddress = apiEndpoint.Text; };
        applyPresetButton.Click += (_, _) => ApplyApiPreset();
        nextButton.Click += (_, _) => TryAdvance();
        backButton.Click += (_, _) => GoBack();
        cancelButton.Click += (_, _) => CancelSetup();
        FormClosing += (_, _) => { if (DialogResult != DialogResult.OK) DialogResult = DialogResult.Cancel; };
    }

    internal AppSettings ReadSettingsDraft()
    {
        var draft = initial.Copy();
        draft.TranslationBackend = SelectedBackend;
        draft.ProcessingMode = SelectedMode;
        draft.TargetLanguage = targetLanguage.Text.Trim();
        draft.DesktopMode = desktop.Checked;
        // An abandoned, hidden provider draft must neither block completion nor
        // replace that provider's previously saved credentials.
        if (SelectedBackend == "Codex")
        {
            draft.CodexExecutablePath = codexPath.Text.Trim();
            draft.CodexModel = codexModel.Text.Trim();
            draft.CodexReasoningEffort = codexReasoning.SelectedIndex switch { 1 => "low", 2 => "medium", 3 => "high", _ => "" };
        }
        if (SelectedBackend == "OpenAI")
        {
            draft.ApiBaseUrl = apiEndpoint.Text.Trim();
            draft.ApiModel = apiModel.Text.Trim();
            draft.ApiKey = apiKey.Text.Trim();
            draft.ApiImageDetail = apiImageDetail.SelectedIndex switch { 1 => "High", 2 => "Low", 3 => "Auto", _ => "Default" };
            draft.ApiStream = apiStream.Checked;
        }
        // VR users may deliberately mirror a monitor in the headset. DesktopMode selects the
        // runtime independently, so switching back preserves the preferred VR source.
        draft.CaptureMode = captureMode.SelectedIndex == 1 ? "桌面主显示器" : "VR 左眼";
        draft.Hotkey = hotkey.Text.Trim();
        draft.ControllerEnabled = controller.Checked;
        return draft;
    }

    internal bool TryAdvance()
    {
        if (finishing) return false;
        if (CurrentStep == 2) return TryFinish();
        ClearError();
        try
        {
            if (CurrentStep == 1) { ValidateService(); ValidateChoice(); }
            if (CurrentStep == 0 && SelectedBackend == "Credits" && login.IsAuthenticated) { ShowStep(2); return true; }
            ShowStep(CurrentStep + 1);
            return true;
        }
        catch (Exception error)
        {
            ShowError(error.Message);
            return false;
        }
    }

    internal bool TryFinish()
    {
        if (finishing) return false;
        if (CurrentStep != 2) { ShowError("请先完成前面的步骤，再保存并开始。"); return false; }
        ClearError();
        try
        {
            ValidateService();
            var draft = ReadSettingsDraft();
            SettingsValidation.ValidateForSave(draft, requireServiceConfiguration: true);
            finishing = true;
            nextButton.Enabled = backButton.Enabled = cancelButton.Enabled = false;
            saveSettings(draft);
            DialogResult = DialogResult.OK;
            Close();
            return true;
        }
        catch (Exception error)
        {
            finishing = false;
            nextButton.Enabled = cancelButton.Enabled = true;
            backButton.Enabled = CurrentStep > 0;
            DialogResult = DialogResult.None;
            ShowError(error.Message);
            return false;
        }
    }

    internal void GoBack()
    {
        if (finishing || CurrentStep == 0) return;
        if (CurrentStep == 1 && SelectedBackend == "Credits") login.CancelPending();
        ShowStep(CurrentStep == 2 && SelectedBackend == "Credits" ? 0 : CurrentStep - 1);
    }

    internal void CancelSetup()
    {
        if (finishing) return;
        DialogResult = DialogResult.Cancel;
        Close();
    }

    internal void ApplyApiPreset()
    {
        if (apiPreset.SelectedIndex < 0) return;
        var preset = ApiProviderPresets.All[apiPreset.SelectedIndex];
        providerNote.Text = preset.Note;
        if (preset.BaseUrl.Length == 0) return;
        apiEndpoint.Text = preset.BaseUrl;
        apiImageDetail.SelectedIndex = 0;
        apiStream.Checked = false;
        providerNote.Text = preset.Note;
        ClearError();
    }

    private string SelectedBackend => serviceChoices[1].Checked ? "OpenAI" : serviceChoices[2].Checked ? "Codex"
        : serviceChoices[3].Checked ? "GoogleTranslate" : "Credits";
    private string SelectedMode => processingMode.SelectedIndex switch { 1 => "Image", 2 => "Handwriting", 3 => "LocalJapanese", _ => "Ocr" };

    private void ValidateChoice()
    {
        if (string.IsNullOrWhiteSpace(targetLanguage.Text) || targetLanguage.Text.Trim().Length > 100)
            throw new InvalidOperationException("请填写目标语言，例如“简体中文”，最多 100 个字符。");
        if (SelectedBackend == "GoogleTranslate" && SelectedMode is "Image" or "Handwriting")
            throw new InvalidOperationException("Google Translate 只能翻译文字。请选择“本地 OCR”或“本地日语手写 OCR”，或改用支持图片的翻译服务。");
    }

    private void ValidateService()
    {
        if (SelectedBackend == "Credits")
        {
            if (!login.IsAuthenticated)
            {
                if (CurrentStep == 2) ShowStep(1);
                throw new InvalidOperationException("请先登录账号。");
            }
            return;
        }
        if (SelectedBackend == "Codex")
        {
            if (codexModel.Text.Trim().Length > 256 || codexModel.Text.Any(char.IsControl))
                throw new InvalidOperationException("Codex 模型名称无效，请检查输入。");
            if (codexPath.Text.Trim().Length > 4096)
                throw new InvalidOperationException("Codex 程序路径过长，请检查输入。");
            return;
        }
        if (SelectedBackend == "GoogleTranslate") return;
        Uri endpoint = TranslationClient.GetEndpoint(apiEndpoint.Text.Trim());
        if (string.IsNullOrWhiteSpace(apiModel.Text) || apiModel.Text.Trim().Length > 256 || apiModel.Text.Any(char.IsControl))
            throw new InvalidOperationException("请填写此服务实际可用的模型名称。");
        if (!endpoint.IsLoopback && string.IsNullOrWhiteSpace(apiKey.Text))
            throw new InvalidOperationException("请填写此云端服务的 API 密钥。本机服务未启用鉴权时可以留空。");
        if (apiKey.Text.Any(char.IsControl)) throw new InvalidOperationException("API 密钥不能包含换行或控制字符。");
    }

    private void InitializeChoices()
    {
        processingMode.Items.AddRange(["本地OCR（不推荐）", "直接发送截图给模型", "手写截图直译（AI）", "本地日语手写 OCR → 翻译"]);
        codexReasoning.Items.AddRange(["自动（按阶段）", "low", "medium", "high"]);
        apiPreset.Items.AddRange(ApiProviderPresets.All.Select(preset => (object)preset.Name).ToArray());
        apiImageDetail.Items.AddRange(["服务默认", "高清", "低清", "自动"]);
        captureMode.Items.AddRange(["VR 左眼", "桌面主显示器"]);
        codexPath.PlaceholderText = "留空自动查找 Codex CLI";
        codexModel.PlaceholderText = "留空使用 CLI 默认模型，或填写可用模型名称";
        apiEndpoint.PlaceholderText = "https://…/v1 或 http://localhost:…/v1";
        apiModel.PlaceholderText = "填写所选服务实际支持的模型名称";
        apiKey.PlaceholderText = "本机服务未启用鉴权时可留空";
        apiKey.UseSystemPasswordChar = true;
        targetLanguage.PlaceholderText = "例如：简体中文 / 日本語 / English";
        hotkey.PlaceholderText = "例如 Ctrl+Alt+T";
    }

    private void LoadInitialValues()
    {
        serviceChoices[initial.TranslationBackend switch { "OpenAI" => 1, "Codex" => 2, "GoogleTranslate" => 3, _ => 0 }].Checked = true;
        desktop.Checked = firstRun || initial.DesktopMode;
        headset.Checked = !desktop.Checked;
        processingMode.SelectedIndex = initial.ProcessingMode switch { "Image" => 1, "Handwriting" => 2, "LocalJapanese" => 3, _ => 0 };
        targetLanguage.Text = initial.TargetLanguage;
        codexPath.Text = initial.CodexExecutablePath;
        codexModel.Text = initial.CodexModel;
        codexReasoning.SelectedIndex = initial.CodexReasoningEffort switch { "low" => 1, "medium" => 2, "high" => 3, _ => 0 };
        apiPreset.SelectedIndex = 0;
        apiEndpoint.Text = credentialAddress = initial.ApiBaseUrl;
        apiModel.Text = initial.ApiModel;
        apiKey.Text = initial.ApiKey;
        apiImageDetail.SelectedIndex = initial.ApiImageDetail switch { "High" => 1, "Low" => 2, "Auto" => 3, _ => 0 };
        apiStream.Checked = initial.ApiStream;
        captureMode.SelectedIndex = initial.CaptureMode == "桌面主显示器" ? 1 : 0;
        hotkey.Text = initial.Hotkey;
        controller.Checked = initial.ControllerEnabled;
        providerNote.Text = ApiProviderPresets.All[0].Note;

    }

    private void BuildShell()
    {
        var shell = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4,
            Padding = new Padding(32, 16, 32, 22), Margin = Padding.Empty };
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.AutoSize)); shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.AutoSize)); shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(shell);
        shell.SizeChanged += (_, _) => {
            int inset = Math.Max(LogicalToDeviceUnits(24), (shell.Width - LogicalToDeviceUnits(600)) / 2);
            if (shell.Padding.Left != inset) shell.Padding = new Padding(inset, shell.Padding.Top, inset, shell.Padding.Bottom);
        };
        var header = Stack();
        stepCount.ForeColor = UiTheme.Accent; stepCount.Font = new Font(Font.FontFamily, 9f);
        pageTitle.ForeColor = UiTheme.Ink; pageTitle.Font = new Font(Font.FontFamily, 20f, FontStyle.Bold);
        pageTitle.Margin = new Padding(0, 4, 0, 20);
        Add(header, stepCount); Add(header, pageTitle); shell.Controls.Add(header, 0, 0);
        shell.Controls.Add(viewport, 0, 1);
        errorLabel.ForeColor = Color.FromArgb(255, 174, 161); errorLabel.Padding = new Padding(0, 8, 0, 2);
        errorLabel.Visible = false; shell.Controls.Add(errorLabel, 0, 2);
        shell.SizeChanged += (_, _) => errorLabel.MaximumSize = new Size(Math.Max(160, shell.ClientSize.Width - shell.Padding.Horizontal - 8), 0);
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 4, Margin = new Padding(0, 16, 8, 0) };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        cancelButton.Text = firstRun ? "退出" : "取消";
        cancelButton.Margin = Padding.Empty;
        footer.Controls.Add(cancelButton, 0, 0); footer.Controls.Add(backButton, 2, 0); footer.Controls.Add(nextButton, 3, 0);
        shell.Controls.Add(footer, 0, 3);
    }

    private TableLayoutPanel BuildChoicePage()
    {
        var page = Stack();
        for (int index = 0; index < serviceChoices.Length; index++)
        {
            var option = serviceChoices[index]; option.TabIndex = index;
            option.AccessibleDescription = option.Description;
            option.AutoSize = true; Add(page, option);
        }
        return page;
    }

    private TableLayoutPanel BuildServicePage()
    {
        var page = Stack();
        loginPage = Stack();
        if (!login.RegistrationAvailable)
            Add(loginPage, Note(login.Enabled ? "登录到 " + CreditServiceClient.NormalizeServerUrl(initial.CreditServerUrl)
                : "已保存的服务地址无效。请返回选择其他服务，进入工作台后再修改地址。", "wizardCustomLoginOrigin"));
        Add(loginPage, login);
        codexCard = Card(page, "使用已登录的 Codex", "无需在此填写 API 密钥；模型调用仍需要网络");
        Row(codexCard, "程序路径", codexPath);
        Row(codexCard, "模型", codexModel);
        Row(codexCard, "思考程度", codexReasoning);
        Row(codexCard, "", Note("留空路径会自动查找 Codex CLI；请先在 Codex 中完成登录。\n模型留空使用 CLI 默认选择，不继承个人配置文件的模型。自动思考按阶段调整；显式选定后用于全部 Codex 识别与翻译。"), true);

        apiCard = Card(page, "连接其他 AI API", "支持 OpenAI 兼容的云端与本机服务");
        var presets = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Fill, Margin = Padding.Empty };
        presets.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); presets.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        apiPreset.Dock = DockStyle.Fill; presets.Controls.Add(apiPreset, 0, 0); presets.Controls.Add(applyPresetButton, 1, 0);
        Row(apiCard, "服务商预设", presets);
        Row(apiCard, "", providerNote, true);
        Row(apiCard, "API 地址", apiEndpoint);
        Row(apiCard, "模型名称", apiModel);
        Row(apiCard, "API 密钥", apiKey);
        Row(apiCard, "图片精度", apiImageDetail);
        Row(apiCard, "返回方式", apiStream);
        Row(apiCard, "", Note("地址填写 Base URL 或完整 /chat/completions 路径。\n本机服务未启用鉴权时可留空密钥；云端服务需要对应密钥。截图翻译需要支持图片输入，手写原文精读需要支持多图。这里仅保存配置。"), true);

        googleCard = Card(page, "Google Translate 后台网页", "适合先在本机识别，再翻译原文");
        Row(googleCard, "", Note("无需 API 密钥。程序在独立 WebView2 网页中输入识别文字、读取译文，不抢占桌面鼠标。\n图片留在本机，只有识别原文发送给 Google Translate。\n需要网络和 WebView2 Runtime；网页验证、限流或页面变化可能使翻译暂时不可用。\n此服务不识别图片，请使用本地 OCR 或本地日语手写 OCR。"), true);
        var recognition = Card(page, "识图与语言", "选好后，截图就交给所选服务处理");
        Row(recognition, "识图方式", processingMode);
        Row(recognition, "目标语言", targetLanguage);
        Row(recognition, "", modeNote, true);
        return page;
    }

    private void ServiceChanged()
    {
        if (SelectedBackend == "GoogleTranslate" && SelectedMode is "Image" or "Handwriting")
        {
            modeBeforeGoogle = processingMode.SelectedIndex;
            processingMode.SelectedIndex = 0;
        }
        else if (SelectedBackend != "GoogleTranslate" && modeBeforeGoogle is int previous)
        {
            modeBeforeGoogle = null;
            processingMode.SelectedIndex = previous;
        }
        RefreshServiceChoices();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        if (StartPosition == FormStartPosition.Manual) return;
        var area = (Owner is null ? Screen.FromControl(this) : Screen.FromControl(Owner)).WorkingArea;
        int gap = LogicalToDeviceUnits(8);
        var limit = new Size(Math.Max(1, area.Width - 2 * gap), Math.Max(1, area.Height - 2 * gap));
        MinimumSize = new Size(Math.Min(MinimumSize.Width, limit.Width), Math.Min(MinimumSize.Height, limit.Height));
        Size = new Size(Math.Min(Width, limit.Width), Math.Min(Height, limit.Height));
        var anchor = Owner?.Bounds ?? area;
        Location = new Point(Math.Clamp(anchor.X + (anchor.Width - Width) / 2, area.Left, area.Right - Width),
            Math.Clamp(anchor.Y + (anchor.Height - Height) / 2, area.Top, area.Bottom - Height));
    }

    private TableLayoutPanel BuildCapturePage()
    {
        var page = Stack();
        Add(page, desktop);
        Add(page, headset);
        desktop.AutoSize = headset.AutoSize = true;
        desktop.AccessibleDescription = desktop.Description;
        headset.AccessibleDescription = headset.Description;
        triggerCard = Card(page, "快捷键", "");
        Row(triggerCard, "截图并翻译", hotkey);
        Row(triggerCard, "头显内画面", captureMode);
        Row(triggerCard, "手柄", controller);
        Row(triggerCard, "", triggerNote, true);
        disclosure.Font = new Font(Font.FontFamily, 8.5f);
        disclosure.Margin = new Padding(2, 2, 0, 8); Add(page, disclosure);
        desktop.CheckedChanged += (_, _) => RefreshSummary();
        captureMode.SelectedIndexChanged += (_, _) => RefreshSummary();
        controller.CheckedChanged += (_, _) => RefreshSummary();
        return page;
    }

    private void RefreshServiceChoices()
    {
        codexCard.Visible = SelectedBackend == "Codex";
        apiCard.Visible = SelectedBackend == "OpenAI";
        googleCard.Visible = SelectedBackend == "GoogleTranslate";

        bool incompatible = SelectedBackend == "GoogleTranslate" && SelectedMode is "Image" or "Handwriting";
        modeNote.ForeColor = incompatible ? Color.FromArgb(255, 174, 161) : UiTheme.Muted;
        modeNote.Text = incompatible
            ? "Google Translate 只接受文字。请改选本地 OCR 或本地日语手写 OCR，再继续。"
            : SelectedMode switch
            {
                "Image" => "直接识图：发送截图，由视觉模型组织翻译内容。",
                "Handwriting" => "AI 手写：发送一张保留笔画细节的截图，一次请求直接返回译文。核对原文可单独选择「仅识别手写（AI）」。",
                "LocalJapanese" => "本地日语 OCR：使用 NDLOCR-Lite 在电脑上识别日文。复杂手写仍可能误读，请核对原文。",
                _ => "本地 OCR：使用 Tesseract 识别印刷文字，再发送原文翻译。潦草手写建议尝试 AI 手写识别。"
            };
        if (SelectedBackend == "Credits") modeNote.Text = SelectedMode is "Image" or "Handwriting"
            ? "积分服务一次请求直接翻译截图。成功扣 1 分；无字、失败或完成前取消不扣。"
            : "先在本机识别，再把原文发给积分服务。成功翻译扣 1 分；仅本地识别不扣。";
        ClearError();
        RefreshSummary();
    }

    private void RefreshSummary()
    {
        if (loading) return;
        var draft = ReadSettingsDraft();
        var rows = (TableLayoutPanel)triggerCard.Controls[0];
        for (int row = 1; row <= 2; row++)
            for (int col = 0; col < 2; col++)
                if (rows.GetControlFromPosition(col, row) is { } control) control.Visible = !draft.DesktopMode;
        triggerCard.Text = draft.DesktopMode ? "桌面快捷键" : "SteamVR 触发方式";
        disclosure.Text = (SelectedMode is "Image" or "Handwriting" ? "截图会发送给" : "仅识别出的原文会发送给") +
            (SelectedBackend switch { "Credits" => "积分服务及其 AI 提供方。", "Codex" => "本机 Codex 配置的模型。", "GoogleTranslate" => "Google 翻译。", _ => "你配置的模型服务。" });
        if (draft.DesktopMode) { triggerNote.Text = "在其他应用中也能直接截图，译文会显示在桌面。"; return; }
        triggerNote.Text = !draft.ControllerEnabled
            ? "手柄触发已关闭。可使用上方键盘快捷键或工作台按钮截图。"
            : draft.SpatialSelectionEnabled
                ? "已保留双手柄框选设置：按住左 X 指向矩形对角，松开后截图。可在主界面“截图与按键”调整。"
                : "左 X 按住 1.5 秒：圆环填满后截图翻译。继续按到 3 秒：取消本次并隐藏字幕。\n先松开，再长按即可截取新画面。Index 使用左 A，Vive 使用右菜单键。";
    }

    private void ShowStep(int step)
    {
        CurrentStep = Math.Clamp(step, 0, 2);
        bool signingIn = CurrentStep == 1 && SelectedBackend == "Credits";
        if (!signingIn) login.CancelPending();
        ClearError();
        viewport.SuspendLayout();
        for (int index = 0; index < pages.Length; index++)
        {
            if (pages[index].Parent is null) viewport.Controls.Add(pages[index]);
            pages[index].Visible = index == CurrentStep && !signingIn;
        }
        if (loginPage.Parent is null) viewport.Controls.Add(loginPage);
        loginPage.Visible = signingIn;
        (signingIn ? loginPage : pages[CurrentStep]).BringToFront();
        viewport.AutoScrollPosition = Point.Empty;
        viewport.ResumeLayout(true);
        pageTitle.Text = CurrentStep switch
        {
            0 => "选择翻译服务",
            1 => signingIn ? "登录账号" : "设置翻译服务",
            _ => "选择使用画面"
        };
        stepCount.Text = $"初次设置    {CurrentStep + 1} / 3";
        if (!firstRun) stepCount.Text = $"配置    {CurrentStep + 1} / 3";
        backButton.Visible = CurrentStep > 0;
        cancelButton.Visible = !signingIn;
        nextButton.Visible = !signingIn;
        nextButton.Text = CurrentStep == 2 ? "开始使用" : "下一步";
        AcceptButton = signingIn ? login.LoginButton : nextButton;
        CancelButton = signingIn ? backButton : cancelButton;
        RefreshSummary();
        if (signingIn) { if (Visible) login.FocusUsername(); return; }
        Control focus = CurrentStep switch
        {
            0 => serviceChoices.Single(choice => choice.Checked),
            1 => SelectedBackend switch { "OpenAI" => apiEndpoint, "Codex" => codexModel, _ => processingMode },
            _ => desktop.Checked ? desktop : headset
        };
        if (Visible) focus.Focus();
    }

    private void ShowError(string message)
    {
        errorLabel.Text = message;
        errorLabel.Visible = true;
    }

    private void ClearError() { errorLabel.Text = ""; errorLabel.Visible = false; }

    protected override void Dispose(bool disposing)
    {
        if (disposing && loginPage is { Parent: null, IsDisposed: false }) loginPage.Dispose();
        if (disposing)
            foreach (var page in pages)
                if (!page.IsDisposed && page.Parent is null) page.Dispose();
        base.Dispose(disposing);
    }

    private static TableLayoutPanel Stack()
    {
        var stack = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Margin = Padding.Empty };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return stack;
    }

    private static void Add(TableLayoutPanel stack, Control child)
    {
        int row = stack.RowCount++;
        stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        child.Dock = DockStyle.Top;
        stack.Controls.Add(child, 0, row);
        if (child is Label label)
            stack.SizeChanged += (_, _) => label.MaximumSize = new Size(Math.Max(160, stack.ClientSize.Width - label.Margin.Horizontal), 0);
    }

    private static UiCard Card(TableLayoutPanel stack, string title, string caption)
    {
        var card = new UiCard { Text = title, Caption = caption, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            HeaderHeight = caption.Length == 0 ? 42 : 62, Padding = new Padding(16, caption.Length == 0 ? 42 : 62, 16, 12), Margin = new Padding(0, 0, 0, 14) };
        var rows = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top, ColumnCount = 2, Margin = Padding.Empty };
        rows.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120)); rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        card.Controls.Add(rows); Add(stack, card);
        return card;
    }

    private static void Row(UiCard card, string label, Control value, bool fullWidth = false)
    {
        var rows = (TableLayoutPanel)card.Controls[0]; int row = rows.RowCount++;
        rows.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        if (!fullWidth) rows.Controls.Add(new Label { Text = label, AutoSize = true, ForeColor = UiTheme.Muted, Margin = new Padding(0, 12, 12, 8) }, 0, row);
        Control container = value;
        if (value is TextBox input)
        {
            var frame = new UiCard { HeaderHeight = 0, Height = 42, BackColor = UiTheme.Input, Padding = new Padding(12, 10, 12, 8) };
            input.Dock = DockStyle.Fill; frame.Controls.Add(input); container = frame;
        }
        if (value is Label note)
            rows.SizeChanged += (_, _) => note.MaximumSize = new Size(Math.Max(160, rows.ClientSize.Width - (fullWidth ? 0 : (int)rows.ColumnStyles[0].Width) - 8), 0);
        container.Margin = new Padding(0, 4, 0, 7);
        container.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        if (container is CheckBox) container.Margin = new Padding(0, 10, 0, 10);
        rows.Controls.Add(container, fullWidth ? 0 : 1, row);
        if (fullWidth) rows.SetColumnSpan(container, 2);
    }

    private static Label Note(string text, string name = "") => new()
    {
        Name = name, Text = text, AutoSize = true, ForeColor = UiTheme.Muted,
        Margin = new Padding(0, 4, 0, 8), UseMnemonic = false
    };

    private static UiButton Button(string text, string name, bool primary = false) => new()
    {
        Name = name, Text = text, Primary = primary, AutoSize = true,
        MinimumSize = new Size(100, 42), Margin = new Padding(8, 0, 0, 0), UseMnemonic = false
    };

    private static ComboBox Combo(string name) => new UiComboBox
    {
        Name = name, DropDownStyle = ComboBoxStyle.DropDownList, Width = 360, Margin = Padding.Empty,
        IntegralHeight = false, DropDownHeight = 240
    };

    private static TextBox Input(string name) => new()
    {
        Name = name, Width = 360, BorderStyle = BorderStyle.None, BackColor = UiTheme.Input,
        ForeColor = UiTheme.Ink, Margin = Padding.Empty
    };

    private static CheckBox Check(string text, string name) => new()
    {
        Name = name, Text = text, AutoSize = true, ForeColor = UiTheme.Ink, UseVisualStyleBackColor = false
    };
}
