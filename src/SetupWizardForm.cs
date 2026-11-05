namespace SteamVrTranslator;

/// <summary>A staged settings editor. Account access is explicit; opening or saving never calls a model.</summary>
internal sealed class SetupWizardForm : Form
{
    private readonly AppSettings initial;
    private readonly Action<AppSettings> saveSettings;
    private readonly bool firstRun;
    private readonly Func<string, CreditSession?> loadSession;
    private readonly Panel viewport = new() { Name = "wizardViewport", Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(0, 0, 8, 0) };
    private readonly Label errorLabel = Note("", "wizardError");
    private readonly Label pageTitle = Note("", "wizardPageTitle");
    private readonly Label modeNote = Note("", "wizardModeNote");
    private readonly Label providerNote = Note("", "wizardProviderNote");
    private readonly Label summary = Note("", "wizardSummary");
    private readonly Label disclosure = Note("", "wizardDisclosure");
    private readonly Label triggerNote = Note("", "wizardTriggerNote");
    private readonly Label[] stepLabels = new Label[3];
    private readonly TableLayoutPanel[] pages = [];
    private readonly UiButton nextButton = Button("下一步", "wizardNext", true);
    private readonly UiButton backButton = Button("返回", "wizardBack");
    private readonly UiButton cancelButton = Button("取消", "wizardCancel");
    internal const string OfficialCreditServerUrl = "https://api.mugonzei.top";
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
    private readonly TextBox creditServer = Input("wizardCreditServerUrl");
    private readonly CheckBox creditAdvanced = Check("使用其他积分服务器", "wizardCreditAdvanced");
    private readonly UiButton openAccount = Button("登录 / 注册 / 兑换积分", "wizardOpenAccount", true);
    private readonly Label accountStatus = Note("", "wizardAccountStatus");
    private readonly Label creditDestination = Note("", "wizardCreditDestination");
    private UiCard creditAddressCard = null!;
    private readonly ComboBox apiImageDetail = Combo("wizardApiImageDetail");
    private readonly CheckBox apiStream = Check("使用流式接口（部分模型需要）", "wizardApiStream");
    private readonly ComboBox captureMode = Combo("wizardCaptureMode");
    private readonly TextBox hotkey = Input("wizardHotkey");
    private readonly CheckBox controller = Check("启用 VR 手柄按键", "wizardControllerEnabled");
    private UiCard codexCard = null!, apiCard = null!, googleCard = null!, creditCard = null!;
    private string credentialAddress = "";
    private bool loading = true, finishing;
    private int? modeBeforeGoogle;
    private string customServerDraft = "";

    internal int CurrentStep { get; private set; }
    internal string LastError => errorLabel.Text;

    public SetupWizardForm(AppSettings initialSettings, Action<AppSettings> saveSettings, bool firstRun = true,
        Func<string, CreditSession?>? loadSession = null)
    {
        ArgumentNullException.ThrowIfNull(initialSettings);
        ArgumentNullException.ThrowIfNull(saveSettings);
        initial = initialSettings.Copy();
        if (firstRun)
        {
            initial.TranslationBackend = "Credits";
            initial.CreditServerUrl = OfficialCreditServerUrl;
            initial.ProcessingMode = "Handwriting";
        }
        this.saveSettings = saveSettings;
        this.firstRun = firstRun;
        this.loadSession = loadSession ?? (url => new CreditSessionStore().Load(url));

        Text = firstRun ? "无言势翻译器 · 初次设置" : "无言势翻译器 · 重新配置";
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Microsoft YaHei UI", 10f);
        BackColor = UiTheme.Background;
        ForeColor = UiTheme.Ink;
        MinimumSize = new Size(820, 640);
        Size = new Size(1040, 820);
        StartPosition = firstRun ? FormStartPosition.CenterScreen : FormStartPosition.CenterParent;
        ShowInTaskbar = firstRun;
        MinimizeBox = false;
        Icon = SystemIcons.Application;
        DoubleBuffered = true;

        InitializeChoices();
        pages = [BuildChoicePage(), BuildServicePage(), BuildCapturePage()];
        BuildShell();
        LoadInitialValues();
        loading = false;
        RefreshServiceChoices();
        ShowStep(0);

        foreach (var choice in serviceChoices)
            choice.CheckedChanged += (_, _) => { if (!loading && choice.Checked) ServiceChanged(); };
        processingMode.SelectedIndexChanged += (_, _) => { if (!loading) RefreshServiceChoices(); };
        creditAdvanced.CheckedChanged += (_, _) =>
        {
            if (!creditAdvanced.Checked)
            {
                customServerDraft = creditServer.Text;
                creditServer.Text = OfficialCreditServerUrl;
            }
            else if (creditServer.Text.TrimEnd('/') == OfficialCreditServerUrl && customServerDraft.Length > 0)
                creditServer.Text = customServerDraft;
            creditAddressCard.Visible = SelectedBackend == "Credits" && creditAdvanced.Checked;
            RefreshAccountStatus();
        };
        openAccount.Click += (_, _) => OpenAccount();
        creditServer.Leave += (_, _) => RefreshAccountStatus();
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
        AcceptButton = nextButton;
        CancelButton = cancelButton;
        FormClosing += (_, _) => { if (DialogResult != DialogResult.OK) DialogResult = DialogResult.Cancel; };
    }

    internal AppSettings ReadSettingsDraft()
    {
        var draft = initial.Copy();
        draft.TranslationBackend = SelectedBackend;
        draft.ProcessingMode = SelectedMode;
        draft.TargetLanguage = targetLanguage.Text.Trim();
        if (SelectedBackend == "Credits") draft.CreditServerUrl = creditServer.Text.Trim();
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
            if (CurrentStep == 1) { ValidateChoice(); ValidateService(); }
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
        if (!finishing && CurrentStep > 0) ShowStep(CurrentStep - 1);
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
            _ = CreditServiceClient.NormalizeServerUrl(creditServer.Text.Trim());
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
        processingMode.Items.AddRange(["本地OCR（不推荐）", "直接发送截图给模型", "手写体 OCR（AI）→ 翻译", "本地日语手写 OCR → 翻译"]);
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
        creditServer.PlaceholderText = "填写运营者提供的服务端地址";
        targetLanguage.PlaceholderText = "例如：简体中文 / 日本語 / English";
        hotkey.PlaceholderText = "例如 Ctrl+Alt+T";
    }

    private void LoadInitialValues()
    {
        serviceChoices[initial.TranslationBackend switch { "OpenAI" => 1, "Codex" => 2, "GoogleTranslate" => 3, _ => 0 }].Checked = true;
        creditServer.Text = initial.CreditServerUrl;
        creditAdvanced.Checked = initial.CreditServerUrl.TrimEnd('/') != OfficialCreditServerUrl;
        creditAddressCard.Visible = creditAdvanced.Checked;
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
        RefreshAccountStatus();
    }

    private void BuildShell()
    {
        var frame = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
            Padding = new Padding(18), Margin = Padding.Empty };
        frame.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 206));
        frame.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        frame.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(frame);
        var sidebar = new UiCard { Dock = DockStyle.Fill, HeaderHeight = 0, BackColor = UiTheme.Sidebar,
            Padding = new Padding(20, 22, 20, 20), Margin = new Padding(0, 0, 18, 0) };
        frame.Controls.Add(sidebar, 0, 0);
        var rail = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Margin = Padding.Empty };
        rail.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        rail.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        rail.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        rail.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        rail.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        rail.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        sidebar.Controls.Add(rail);
        frame.SizeChanged += (_, _) =>
        {
            int width = frame.ClientSize.Width < LogicalToDeviceUnits(860) ? 0 : LogicalToDeviceUnits(206);
            if (Math.Abs(frame.ColumnStyles[0].Width - width) < .5f) return;
            frame.SuspendLayout();
            frame.ColumnStyles[0].Width = width; sidebar.Visible = width > 0;
            frame.ResumeLayout(true);
        };
        var brand = Note("M U G O N Z E I", "wizardBrand");
        brand.ForeColor = UiTheme.Accent; brand.Font = new Font(Font.FontFamily, 8f, FontStyle.Bold);
        brand.Margin = new Padding(0, 0, 0, 14); rail.Controls.Add(brand, 0, 0);
        var heading = Note("无言势\n翻译器", "wizardHeading");
        heading.Font = new Font(Font.FontFamily, 24f, FontStyle.Bold); heading.ForeColor = UiTheme.Ink;
        heading.Margin = new Padding(0, 0, 0, 34); rail.Controls.Add(heading, 0, 1);
        var steps = Stack();
        string[] labels = ["01  选择服务\n      选一种适合你的方式", "02  连接与识图\n      准备账号或服务", "03  截图与开始\n      确认后进入工作台"];
        for (int index = 0; index < labels.Length; index++)
        {
            var label = Note(labels[index], "wizardStep" + index);
            label.Padding = new Padding(10, 14, 4, 14); label.Margin = new Padding(0, 0, 0, 10);
            label.Font = new Font(Font.FontFamily, 8.5f); stepLabels[index] = label; Add(steps, label);
        }
        rail.Controls.Add(steps, 0, 2);
        var tip = Note("长按左 X，读懂眼前。\n1.5 秒截图 · 3 秒取消");
        tip.Font = new Font(Font.FontFamily, 8.5f); tip.MaximumSize = new Size(160, 0); rail.Controls.Add(tip, 0, 4);

        var shell = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4,
            Padding = new Padding(4, 14, 2, 2), Margin = Padding.Empty };
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.AutoSize)); shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.AutoSize)); shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        frame.Controls.Add(shell, 1, 0);
        var header = Stack();
        var eyebrow = Note(firstRun ? "初次见面，三步准备。" : "重新配置，继续交流。", "wizardIntro");
        eyebrow.ForeColor = UiTheme.Accent; eyebrow.Font = new Font(Font.FontFamily, 9f);
        pageTitle.ForeColor = UiTheme.Ink; pageTitle.Font = new Font(Font.FontFamily, 20f, FontStyle.Bold);
        pageTitle.Margin = new Padding(0, 4, 0, 22);
        Add(header, eyebrow); Add(header, pageTitle); shell.Controls.Add(header, 0, 0);
        shell.Controls.Add(viewport, 0, 1);
        errorLabel.ForeColor = Color.FromArgb(255, 174, 161); errorLabel.Padding = new Padding(0, 8, 0, 2);
        errorLabel.Visible = false; shell.Controls.Add(errorLabel, 0, 2);
        shell.SizeChanged += (_, _) => errorLabel.MaximumSize = new Size(Math.Max(160, shell.ClientSize.Width - shell.Padding.Horizontal - 8), 0);
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 4, Margin = new Padding(0, 16, 8, 0) };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        cancelButton.Text = firstRun ? "退出设置" : "取消";
        cancelButton.Margin = Padding.Empty;
        footer.Controls.Add(cancelButton, 0, 0); footer.Controls.Add(backButton, 2, 0); footer.Controls.Add(nextButton, 3, 0);
        shell.Controls.Add(footer, 0, 3);
    }

    private TableLayoutPanel BuildChoicePage()
    {
        var page = Stack();
        Add(page, Note("选择后可继续配置。以后也能在工作台随时切换。"));
        for (int index = 0; index < serviceChoices.Length; index++)
        {
            var option = serviceChoices[index]; option.TabIndex = index;
            option.AccessibleDescription = option.Description;
            option.AutoSize = true; Add(page, option);
        }
        var help = Note("中 / 日 / 英识别与翻译 · 支持手柄和键盘触发", "wizardChoiceHint");
        help.Font = new Font(Font.FontFamily, 8.5f); help.Margin = new Padding(4, 10, 0, 10); Add(page, help);
        return page;
    }

    private TableLayoutPanel BuildServicePage()
    {
        var page = Stack();
        creditCard = Card(page, "用一个账号，开始翻译", "无需自备模型或 API 密钥");
        Row(creditCard, "", Note("完整翻译成功扣 1 积分；无字、失败或完成前取消不扣。\n可使用兑换码获取积分，充值方案以账户窗口实际显示为准。"), true);
        Row(creditCard, "", openAccount, true);
        accountStatus.ForeColor = UiTheme.Accent;
        Row(creditCard, "", accountStatus, true);
        Row(creditCard, "", creditDestination, true);
        Row(creditCard, "", creditAdvanced, true);
        creditAddressCard = Card(page, "其他积分服务器", "只在使用其他运营者或本机开发服务时修改");
        Row(creditAddressCard, "服务地址", creditServer);
        Row(creditAddressCard, "", Note("登录会话按服务地址独立保存。修改地址后，请重新进入账户窗口登录。"), true);
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
        Row(apiCard, "", Note("地址填写 Base URL 或完整 /chat/completions 路径。\n本机服务未启用鉴权时可留空密钥；云端服务需要对应密钥。AI 手写识别还需模型支持多图输入。这里仅保存配置，不发送测试图片。"), true);

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

    private void OpenAccount()
    {
        try
        {
            _ = CreditServiceClient.NormalizeServerUrl(creditServer.Text);
            using var account = new CreditAccountForm(creditServer.Text);
            account.ShowDialog(this);
            creditServer.Text = account.ServerUrl.TrimEnd('/');
            creditAdvanced.Checked = creditServer.Text != OfficialCreditServerUrl;
            RefreshAccountStatus();
            ClearError();
        }
        catch (Exception error) { ShowError(error.Message); }
    }

    private void RefreshAccountStatus()
    {
        creditDestination.Text = creditAdvanced.Checked ? "自定义服务 · 请核对下方地址后登录。" : "官方服务 · " + OfficialCreditServerUrl;
        try
        {
            var session = loadSession(creditServer.Text);
            accountStatus.Text = session is null ? "尚未登录 · 可现在登录，也可进入工作台后继续。"
                : $"已保存 {session.Username} 的登录 · 余额和登录有效性可在账户窗口刷新。";
        }
        catch (Exception)
        {
            accountStatus.Text = "请确认服务地址，再打开账户窗口。";
        }
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
        var capture = Card(page, "截图与触发", "保存后进入工作台，连接 SteamVR 即可使用");
        Row(capture, "画面来源", captureMode);
        Row(capture, "键盘快捷键", hotkey);
        Row(capture, "手柄输入", controller);
        Row(capture, "", triggerNote, true);
        var review = Card(page, "确认这次设置", "保存只更新配置，不会立即截图或调用模型");
        summary.ForeColor = UiTheme.Ink;
        Row(review, "", summary, true);
        Row(review, "", disclosure, true);
        captureMode.SelectedIndexChanged += (_, _) => RefreshSummary();
        hotkey.TextChanged += (_, _) => RefreshSummary();
        controller.CheckedChanged += (_, _) => RefreshSummary();
        return page;
    }

    private void RefreshServiceChoices()
    {
        codexCard.Visible = SelectedBackend == "Codex";
        apiCard.Visible = SelectedBackend == "OpenAI";
        googleCard.Visible = SelectedBackend == "GoogleTranslate";
        creditCard.Visible = SelectedBackend == "Credits";
        creditAddressCard.Visible = SelectedBackend == "Credits" && creditAdvanced.Checked;
        bool incompatible = SelectedBackend == "GoogleTranslate" && SelectedMode is "Image" or "Handwriting";
        modeNote.ForeColor = incompatible ? Color.FromArgb(255, 174, 161) : UiTheme.Muted;
        modeNote.Text = incompatible
            ? "Google Translate 只接受文字。请改选本地 OCR 或本地日语手写 OCR，再继续。"
            : SelectedMode switch
            {
                "Image" => "直接识图：发送截图，由视觉模型组织翻译内容。",
                "Handwriting" => "AI 手写：自动定位字迹，结合放大图读取原文，再翻译。需要多次请求，等待和用量通常更多。",
                "LocalJapanese" => "本地日语 OCR：使用 NDLOCR-Lite 在电脑上识别日文。复杂手写仍可能误读，请核对原文。",
                _ => "本地 OCR：使用 Tesseract 识别印刷文字，再发送原文翻译。潦草手写建议尝试 AI 手写识别。"
            };
        if (SelectedBackend == "Credits") modeNote.Text = SelectedMode is "Image" or "Handwriting"
            ? "积分服务在服务器识别原文，再翻译。整张图成功扣 1 分；无字、失败或完成前取消不扣。"
            : "先在本机识别，再把原文发给积分服务。成功翻译扣 1 分；仅本地识别不扣。";
        ClearError();
        RefreshSummary();
    }

    private void RefreshSummary()
    {
        if (loading) return;
        var draft = ReadSettingsDraft();
        string service = SelectedBackend switch { "OpenAI" => "自己的 API 服务", "GoogleTranslate" => "本地 OCR + Google 翻译", "Credits" => "账号积分服务", _ => "本机 Codex" };
        string mode = processingMode.SelectedItem?.ToString() ?? "本地OCR（不推荐）";
        string reasoning = draft.CodexReasoningEffort.Length == 0 ? "自动（按阶段）" : draft.CodexReasoningEffort;
        string model = SelectedBackend == "Codex" ? (draft.CodexModel.Length == 0 ? "CLI 默认模型" : draft.CodexModel) : draft.ApiModel;
        summary.Text = $"翻译服务  ·  {service}\n识图方式  ·  {mode}\n目标语言  ·  {draft.TargetLanguage}\n画面来源  ·  {draft.CaptureMode}\n键盘快捷键  ·  {draft.Hotkey}";
        if (SelectedBackend == "Codex") summary.Text += $"\n模型  ·  {model}　/　思考程度 {reasoning}";
        if (SelectedBackend == "OpenAI") summary.Text += $"\n模型  ·  {model}\nAPI 地址  ·  {draft.ApiBaseUrl}";
        if (SelectedBackend == "Credits") summary.Text += $"\n服务地址  ·  {draft.CreditServerUrl}\n计费  ·  完整翻译成功扣 1 积分";
        string destination = SelectedBackend switch
        {
            "Codex" => "已登录的 Codex 在线服务",
            "GoogleTranslate" => "Google Translate 后台网页",
            "Credits" => "上方配置的积分服务器及其 AI 服务",
            _ => "上方配置的 API 地址"
        };
        disclosure.Text = draft.ProcessingMode is "Image" or "Handwriting"
            ? $"数据去向：截图{(draft.ProcessingMode == "Handwriting" && SelectedBackend != "Credits" ? "及局部细节图" : "")}会发送给{destination}。"
            : $"数据去向：图片在本机识别，只有识别原文发送给{destination}。";
        disclosure.Text += SelectedBackend == "Credits"
            ? "\n积分服务器保存订单、消费流水及近期原文/译文，便于断线后恢复。图片仅用于本次任务。\n任务完成后隐藏字幕不会退款；仅 AI 识别请使用自备服务或本地 OCR。"
            : "\n日常使用不自动建立截图或翻译历史；当前截图可在工作台手动保存。";
        triggerNote.Text = !draft.ControllerEnabled
            ? "手柄触发已关闭。可使用上方键盘快捷键或工作台按钮截图。"
            : draft.SpatialSelectionEnabled
                ? "已保留双手柄框选设置：按住左 X 指向矩形对角，松开后截图。可在主界面“截图与按键”调整。"
                : "左 X 按住 1.5 秒：圆环填满后截图翻译。继续按到 3 秒：取消本次并隐藏字幕。\n先松开，再长按即可截取新画面。Index 使用左 A，Vive 使用右菜单键。";
    }

    private void ShowStep(int step)
    {
        CurrentStep = Math.Clamp(step, 0, 2);
        ClearError();
        viewport.SuspendLayout();
        for (int index = 0; index < pages.Length; index++)
        {
            if (pages[index].Parent is null) viewport.Controls.Add(pages[index]);
            pages[index].Visible = index == CurrentStep;
        }
        pages[CurrentStep].BringToFront();
        viewport.AutoScrollPosition = Point.Empty;
        viewport.ResumeLayout(true);
        pageTitle.Text = CurrentStep switch
        {
            0 => "选择你的翻译服务",
            1 => "连接服务，准备识图",
            _ => "准备好，读懂眼前"
        };
        for (int index = 0; index < stepLabels.Length; index++)
        {
            stepLabels[index].BackColor = index == CurrentStep ? UiTheme.Card : UiTheme.Background;
            stepLabels[index].ForeColor = index == CurrentStep ? UiTheme.Accent : index < CurrentStep ? UiTheme.Ink : UiTheme.Muted;
        }
        backButton.Enabled = CurrentStep > 0;
        nextButton.Text = CurrentStep == 2 ? "保存并进入工作台" : "下一步";
        RefreshSummary();
        Control focus = CurrentStep switch
        {
            0 => serviceChoices.Single(choice => choice.Checked),
            1 => SelectedBackend switch { "OpenAI" => apiEndpoint, "Codex" => codexModel, "Credits" => openAccount, _ => processingMode },
            _ => hotkey
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
            HeaderHeight = 70, Padding = new Padding(20, 70, 20, 18), Margin = new Padding(0, 0, 0, 14) };
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
