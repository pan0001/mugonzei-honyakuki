using System.Diagnostics;

namespace SteamVrTranslator;

internal sealed partial class MainForm
{
    private UiCard codexSettingsCard = null!, apiSettingsCard = null!, localOcrSettingsCard = null!, googleSettingsCard = null!;
    private readonly List<UiButton> navigation = [];
    private readonly Label pageTitle = new() { AutoSize = true };
    private readonly Label pageDescription = new() { AutoSize = true };
    private readonly UiButton saveSettings = new() { Text = "保存设置", Primary = true, AutoSize = true };

    private void BuildUi()
    {
        SuspendLayout();
        var shell = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(shell);
        shell.Controls.Add(BuildSidebar(), 0, 0);

        var main = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(26, 22, 26, 8), Margin = Padding.Empty };
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 90));
        main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        shell.Controls.Add(main, 1, 0);
        var heading = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var headingText = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty };
        pageTitle.Font = new Font(Font.FontFamily, 22, FontStyle.Bold);
        pageTitle.Margin = new Padding(0, 0, 0, 7);
        pageDescription.Font = new Font(Font.FontFamily, 9);
        pageDescription.ForeColor = UiTheme.Muted;
        pageDescription.Margin = Padding.Empty;
        headingText.Controls.AddRange([pageTitle, pageDescription]);
        heading.Controls.Add(headingText, 0, 0);
        saveSettings.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        saveSettings.Margin = new Padding(12, 7, 0, 0);
        saveSettings.Click += (_, _) => TryAction(ApplySettings);
        heading.Controls.Add(saveSettings, 1, 0);
        main.Controls.Add(heading, 0, 0);
        main.Controls.Add(tabs, 0, 1);
        status.AutoSize = false; status.Dock = DockStyle.Fill;
        status.Font = new Font(Font.FontFamily, 8.5f);
        status.ForeColor = UiTheme.Muted; status.TextAlign = ContentAlignment.MiddleLeft;
        status.Padding = new Padding(0, 6, 0, 0); status.Margin = Padding.Empty;
        main.Controls.Add(status, 0, 2);

        BuildWorkspace();
        BuildAiSettings();
        BuildCaptureSettings();
        BuildSubtitleSettings();
        InitializeSettingsControls();
        UiNativeTheme.ApplyDarkWindow(this);
        tabs.SelectedIndexChanged += (_, _) => UpdateNavigation();
        UpdateNavigation();
        ResumeLayout(true);
    }

    private Control BuildSidebar()
    {
        var sidebar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, BackColor = UiTheme.Sidebar, Padding = new Padding(20, 30, 20, 20), Margin = Padding.Empty };
        sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 110));
        sidebar.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        sidebar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        sidebar.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var brand = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty };
        brand.Controls.Add(new Label { Text = "V R  /  T R A N S L A T E", AutoSize = true, ForeColor = Accent, Font = new Font("Segoe UI", 8, FontStyle.Bold), Margin = new Padding(0, 0, 0, 12) });
        brand.Controls.Add(new Label { Text = "无言势翻译器", AutoSize = true, Font = new Font(Font.FontFamily, 16, FontStyle.Bold), Margin = Padding.Empty });
        sidebar.Controls.Add(brand, 0, 0);
        var nav = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty };
        string[] labels = ["翻译工作台", "AI 服务", "截图与按键", "字幕显示"];
        for (int i = 0; i < labels.Length; i++)
        {
            int index = i;
            var button = new UiButton { Text = labels[i], Width = 180, Height = 48, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(22, 8, 12, 8), Margin = new Padding(0, 0, 0, 10) };
            button.Click += (_, _) => tabs.SelectedIndex = index;
            navigation.Add(button); nav.Controls.Add(button);
        }
        sidebar.Controls.Add(nav, 0, 1);
        var footer = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty };
        footer.Controls.Add(new Label { Text = "STEAMVR 连接", AutoSize = true, ForeColor = UiTheme.Muted, Font = new Font("Segoe UI", 8, FontStyle.Bold), Margin = new Padding(0, 0, 0, 10) });
        vrStatus.MaximumSize = new Size(178, 90); vrStatus.AutoEllipsis = true;
        vrStatus.Font = new Font(Font.FontFamily, 8.5f); vrStatus.ForeColor = UiTheme.Muted;
        vrStatus.Text = "等待连接"; vrStatus.Margin = new Padding(0, 0, 0, 12);
        footer.Controls.Add(vrStatus);
        var reconnect = Button("连接 SteamVR"); reconnect.AutoSize = false; reconnect.Size = new Size(180, 40);
        reconnect.Click += (_, _) => ConnectVr(); footer.Controls.Add(reconnect);
        sidebarInputHelp.Font = new Font(Font.FontFamily, 8); sidebarInputHelp.Margin = new Padding(0, 16, 0, 0);
        footer.Controls.Add(sidebarInputHelp);
        sidebar.Controls.Add(footer, 0, 3);
        return sidebar;
    }

    private void UpdateNavigation()
    {
        int index = Math.Max(0, tabs.SelectedIndex);
        string[] titles = ["翻译工作台", "AI 服务", "截图与按键", "字幕显示"];
        string[] descriptions = ["截取眼前画面，或用手柄框选文字并固定译文。", "选择识别方式与翻译服务，支持视觉模型直接读图。", "设置画面来源、识别范围，以及触发翻译的方式。", "设置普通字幕；框选译文保留在原位。"];
        pageTitle.Text = titles[index]; pageDescription.Text = descriptions[index];
        for (int i = 0; i < navigation.Count; i++) navigation[i].Selected = i == index;
        saveSettings.Visible = index != 0;
    }

    private void BuildWorkspace()
    {
        var page = Page("翻译工作台");
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 74));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        page.Controls.Add(layout);
        var actions = Flow();
        ((UiButton)translateButton).Primary = true;
        translateButton.MinimumSize = new Size(156, 44);
        recognizeButton.MinimumSize = new Size(138, 44); cancelButton.MinimumSize = new Size(88, 44);
        actions.Controls.AddRange([translateButton, recognizeButton, cancelButton, saveScreenshotButton]);
        layout.Controls.Add(actions, 0, 0);
        var help = new UiCard { Dock = DockStyle.Fill, HeaderHeight = 0, Padding = new Padding(16, 10, 16, 10), Margin = new Padding(0, 0, 0, 16), BackColor = UiTheme.Sidebar };
        workspaceInputHelp.AutoSize = false; workspaceInputHelp.Dock = DockStyle.Fill;
        workspaceInputHelp.TextAlign = ContentAlignment.MiddleLeft; workspaceInputHelp.Font = new Font(Font.FontFamily, 9);
        help.Controls.Add(workspaceInputHelp);
        layout.Controls.Add(help, 0, 1);
        var columns = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 51)); columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 49));
        columns.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(columns, 0, 2);
        var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 67)); left.RowStyles.Add(new RowStyle(SizeType.Percent, 33));
        preview.BackColor = UiTheme.Input;
        preview.Paint += (_, e) => { if (preview.Image is null) UiPreview.PaintCaptureEmpty(e.Graphics, preview.ClientRectangle, Font); };
        imageBox.Controls.Add(preview); left.Controls.Add(imageBox, 0, 0);
        sourceText.Font = new Font(Font.FontFamily, 10); sourceText.ForeColor = UiTheme.Muted;
        sourceText.PlaceholderText = "识别出的原文会显示在这里";
        var source = new UiCard { Text = "识别原文", Caption = "手写 / 本地 OCR 保留原文，直接识图显示输入说明", Dock = DockStyle.Fill, Margin = new Padding(0, 0, 12, 0) };
        source.Controls.Add(sourceText); left.Controls.Add(source, 0, 1); columns.Controls.Add(left, 0, 0);
        var result = new UiCard { Text = "翻译结果", Caption = "完成后会自动同步到 VR 字幕", Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 0) };
        var resultLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
        resultLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); resultLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        translatedText.Font = new Font(Font.FontFamily, 12);
        translatedText.PlaceholderText = "译文将在这里出现";
        resultLayout.Controls.Add(translatedText, 0, 0);
        var resultActions = Flow(); resultActions.Padding = new Padding(0, 16, 0, 0);
        Button previous = Button("上一页"), next = Button("下一页"), hide = Button("隐藏字幕"), sample = Button("测试字幕");
        foreach (Button button in new[] { previous, next, hide, sample }) { button.Font = new Font(Font.FontFamily, 9); button.MinimumSize = new Size(72, 36); button.Padding = new Padding(10, 8, 10, 8); button.Margin = new Padding(0, 0, 6, 6); }
        previous.Click += (_, _) => ChangePage(-1); next.Click += (_, _) => ChangePage(1);
        hide.Click += (_, _) => TryAction(HideOverlay); sample.Click += (_, _) => ShowDemo();
        resultActions.Controls.AddRange([previous, next, hide, sample]); resultLayout.Controls.Add(resultActions, 0, 1);
        result.Controls.Add(resultLayout); columns.Controls.Add(result, 1, 0);
    }

    private void BuildAiSettings()
    {
        var stack = SettingsPage("AI 服务", 1);
        var general = SettingsCard(stack, "翻译偏好", "选择内容如何被识别，以及交给谁翻译");
        var setup = Button("配置向导");
        setup.Click += (_, _) => TryAction(OpenSetupWizard);
        SettingRow(general, "快速配置", setup);
        SettingRow(general, "翻译服务", backend);
        SettingRow(general, "识图方式", processingMode);
        SettingRow(general, "目标语言", target);
        localOcrSettingsCard = SettingsCard(stack, "本地日语手写", "NDLOCR-Lite · CPU 识别 · 图片留在本机");
        SettingRow(localOcrSettingsCard, "引擎状态", localOcrStatus);
        SettingRow(localOcrSettingsCard, "适用内容", Note("日语手写、横排与竖排。首次识别会加载模型，之后重复使用。\n潦草、模糊或过小的文字仍可能误认；中、日、英混合内容可选择 AI 手写模式。"));
        googleSettingsCard = SettingsCard(stack, "Google Translate", "后台网页 · 免密钥 · 只翻译 OCR 文字");
        SettingRow(googleSettingsCard, "使用方式", Note("后台打开 Google 翻译网页，填入识别原文并读取译文。无需地址或密钥。\n选择本地 OCR 后保存；每次最多 5,000 字，网页验证或拒绝时会停止。"));
        var googleActions = Flow(); googleActions.Controls.AddRange([checkGoogleTranslate, showGoogleBrowser]);
        SettingRow(googleSettingsCard, "网页操作", googleActions);
        SettingRow(googleSettingsCard, "", Note("只发送固定文字 Hello world.，不截取游戏画面。"));
        SettingRow(googleSettingsCard, "", googleTestResult);
        codexSettingsCard = SettingsCard(stack, "本机 Codex", "使用电脑上已登录的 Codex 账号");
        SettingRow(codexSettingsCard, "程序路径", codexPath);
        SettingRow(codexSettingsCard, "模型", codexModel);
        SettingRow(codexSettingsCard, "思考程度", codexReasoningEffort);
        SettingRow(codexSettingsCard, "", Note("自动按识别阶段调整；选定 low、medium 或 high 后，所有 Codex 识别与翻译均使用该程度。保存后生效。"));
        SettingRow(codexSettingsCard, "登录状态", checkCodexLogin);
        apiSettingsCard = SettingsCard(stack, "其他 AI API", "支持云端及本机接口；AI 手写和直接识图需视觉模型，本地 OCR 仅需文字模型");
        var providers = Flow(); apiPreset.Width = 300; providers.Controls.AddRange([apiPreset, useApiPreset]);
        SettingRow(apiSettingsCard, "服务商预设", providers);
        SettingRow(apiSettingsCard, "", apiPresetNote);
        SettingRow(apiSettingsCard, "API 地址", endpoint);
        SettingRow(apiSettingsCard, "模型名称", model);
        SettingRow(apiSettingsCard, "API 密钥", apiKey);
        var options = Flow(); apiStream.Margin = new Padding(0, 8, 0, 8); options.Controls.AddRange([apiImageDetail, apiStream]);
        SettingRow(apiSettingsCard, "请求选项", options);
        var test = Flow(); test.Controls.AddRange([checkApiVision, Note("使用内置测试图，不截取游戏画面")]);
        SettingRow(apiSettingsCard, "连接测试", test);
        SettingRow(apiSettingsCard, "", apiTestResult);
        var info = SettingsCard(stack, "数据与输出", "手写 OCR 先转写再翻译；直接识图由模型组织结果");
        SettingRow(info, "", dataNote, fullWidth: true);
        var more = Flow();
        var local = Button("使用本机 Ollama");
        local.Click += (_, _) => { apiPreset.SelectedIndex = ApiProviderPresets.All.ToList().FindIndex(p => p.BaseUrl.StartsWith("http://localhost:11434", StringComparison.Ordinal)); ApplyApiPreset(); };
        var folder = Button("打开配置目录");
        folder.Click += (_, _) => TryAction(() => { Directory.CreateDirectory(SettingsStore.DirectoryPath); Process.Start(new ProcessStartInfo(SettingsStore.DirectoryPath) { UseShellExecute = true }); });
        more.Controls.AddRange([local, folder]); AddToStack(stack, more);
    }

    private void BuildCaptureSettings()
    {
        var stack = SettingsPage("截图与按键", 2);
        var capture = SettingsCard(stack, "画面与识别", "缩小识别范围，可以避开无关的界面文字");
        SettingRow(capture, "截图来源", captureMode);
        var crop = Flow(); crop.Controls.AddRange([Tiny("左 %"), cropX, Tiny("上 %"), cropY, Tiny("宽 %"), cropW, Tiny("高 %"), cropH]);
        SettingRow(capture, "识别范围", crop);
        SettingRow(capture, "OCR 语言", languages);
        SettingRow(capture, "", Note("eng 英语 · jpn 日语 · kor 韩语 · chi_sim 简中 · chi_tra 繁中\n仅用于普通本地 OCR，使用 + 连接，例如 eng+jpn。\n本地日语手写使用独立模型；AI 手写自动识别中、日、英。两种手写模式均不使用此语言设置。"));
        var inputs = SettingsCard(stack, "触发方式", "手柄可切换拉框或双圈长按；键盘快捷键直接截图");
        SettingRow(inputs, "VR 手柄", controller);
        SettingRow(inputs, "框选模式", spatialSelection);
        var distance = Flow(); distance.Controls.AddRange([selectionDistance, Tiny("米 · 拉框平面距按下时头显的距离")]);
        SettingRow(inputs, "框选距离", distance);
        SettingRow(inputs, "手柄操作", selectionHelp);
        SettingRow(inputs, "", Note("框选始终使用 VR 画面，识别范围由手柄矩形决定；上方截图来源与百分比裁剪仅用于普通截图。"));
        var binding = Button("打开 SteamVR 手柄绑定"); binding.Click += (_, _) => TryAction(() => vr.OpenBindings());
        SettingRow(inputs, "自定义绑定", binding);
        SettingRow(inputs, "翻译快捷键", hotkeyText);
        SettingRow(inputs, "固定快捷键", Note("Ctrl + Alt + ← / →  翻页\nCtrl + Alt + H  显示或隐藏上次译文"));
        SettingRow(inputs, "其他手柄", Note("Index：左 A；Vive：右菜单键。左摇杆 / 左菜单键翻页。"));
    }

    private void BuildSubtitleSettings()
    {
        var stack = SettingsPage("字幕显示", 3);
        var visual = SettingsCard(stack, "视野下方的轻量字幕", "长按时出现进度圆环，翻译后显示半透明字幕条");
        var sample = new UiSubtitlePreview { Dock = DockStyle.Fill, Height = 200, Margin = Padding.Empty };
        SettingRow(visual, "", sample, fullWidth: true);
        var position = SettingsCard(stack, "位置与停留时间", "调整后保存设置，对下一次字幕生效");
        var location = Flow(); location.Controls.AddRange([Tiny("宽度 m"), overlayWidth, Tiny("距离 m"), overlayDistance, Tiny("高度 m"), overlayY]);
        SettingRow(position, "字幕位置", location);
        var timing = Flow(); timing.Controls.AddRange([duration, Tiny("秒 · 设为 0 时一直显示")]);
        SettingRow(position, "显示时长", timing);
        SettingRow(position, "原文对照", showSource);
        SettingRow(position, "", Note("以上位置与停留时间用于普通视角字幕。框选译文固定在选区，不自动到期；新框、取消或隐藏可收起。\n直接识图模式由模型组织内容；长文本会自动分页。手柄或 Ctrl + Alt + ← / → 可翻页。"));
    }

    private TableLayoutPanel SettingsPage(string title, int index)
    {
        var page = Page(title);
        var scroll = new Panel { Name = "pageScroll" + index, Dock = DockStyle.Fill, AutoScroll = true, Margin = Padding.Empty };
        var stack = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = 0, Margin = Padding.Empty, Padding = new Padding(0, 0, 8, 8) };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        scroll.Controls.Add(stack); page.Controls.Add(scroll); return stack;
    }

    private static void AddToStack(TableLayoutPanel stack, Control control)
    {
        int row = stack.RowCount++; stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        control.Dock = DockStyle.Top; stack.Controls.Add(control, 0, row);
    }

    private static UiCard SettingsCard(TableLayoutPanel stack, string title, string caption)
    {
        var card = new UiCard { Text = title, Caption = caption, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 0, 0, 16), Padding = new Padding(20, 70, 20, 18), HeaderHeight = 70 };
        var rows = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top, ColumnCount = 2, RowCount = 0, Margin = Padding.Empty };
        rows.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 126)); rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        card.Controls.Add(rows); AddToStack(stack, card); return card;
    }

    private static void SettingRow(UiCard card, string label, Control value, bool fullWidth = false)
    {
        var rows = (TableLayoutPanel)card.Controls[0]; int row = rows.RowCount++;
        rows.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        if (!fullWidth) rows.Controls.Add(new Label { Text = label, AutoSize = true, ForeColor = UiTheme.Muted, Margin = new Padding(0, 12, 12, 9) }, 0, row);
        Control container = value;
        if (value is TextBox textbox)
        {
            var frame = new UiCard { HeaderHeight = 0, Height = 42, BackColor = UiTheme.Input, Padding = new Padding(12, 10, 12, 8) };
            textbox.Dock = DockStyle.Fill; textbox.Margin = Padding.Empty; frame.Controls.Add(textbox); container = frame;
        }
        if (value is Label note)
        {
            note.MaximumSize = Size.Empty;
            rows.SizeChanged += (_, _) => { note.MaximumSize = new Size(Math.Max(150, rows.ClientSize.Width - (fullWidth ? 0 : 126) - 8), 0); };
        }
        container.Margin = new Padding(0, 4, 0, 6);
        if (container is not System.Windows.Forms.Button && container is not CheckBox) container.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        if (container is CheckBox) container.Margin = new Padding(0, 10, 0, 12);
        rows.Controls.Add(container, fullWidth ? 0 : 1, row);
        if (fullWidth) rows.SetColumnSpan(container, 2);
    }

    private void InitializeSettingsControls()
    {
        apiKey.UseSystemPasswordChar = true;
        apiKey.TextChanged += (_, _) => { apiKeyAddress = endpoint.Text; InvalidateApiTestResult(); };
        endpoint.TextChanged += (_, _) => { if (apiKey.Text.Length > 0 && ApiProviderPresets.ChangesAuthority(apiKeyAddress, endpoint.Text)) apiKey.Clear(); InvalidateApiTestResult(); };
        model.TextChanged += (_, _) => InvalidateApiTestResult();
        target.TextChanged += (_, _) => InvalidateApiTestResult();
        apiImageDetail.SelectedIndexChanged += (_, _) => InvalidateApiTestResult();
        apiStream.CheckedChanged += (_, _) => InvalidateApiTestResult();
        endpoint.PlaceholderText = "https://你的服务/v1 或 http://localhost:11434/v1";
        model.PlaceholderText = "填写服务商支持的视觉模型名称";
        apiKey.PlaceholderText = "本机 Ollama 可留空；保存时使用 Windows 加密";
        backend.Items.AddRange(["本机 Codex", "其他 AI API", "Google Translate（后台网页）"]);
        backend.SelectedIndexChanged += (_, _) => UpdateBackendControls();
        processingMode.Items.AddRange(["本地OCR（不推荐）", "直接发送截图给模型", "手写体 OCR（AI）→ 翻译", "本地日语手写 OCR → 翻译"]);
        processingMode.SelectedIndexChanged += (_, _) => UpdateBackendControls();
        codexPath.PlaceholderText = "留空自动查找，或填写 codex.exe 的完整路径";
        codexModel.PlaceholderText = "留空使用 Codex 默认模型";
        codexReasoningEffort.Items.AddRange(["自动（按阶段）", "low", "medium", "high"]);
        checkCodexLogin.Click += async (_, _) => await CheckCodexLogin();
        apiPreset.Items.AddRange(ApiProviderPresets.All.Select(p => (object)p.Name).ToArray());
        apiPreset.SelectedIndexChanged += (_, _) => { if (apiPreset.SelectedIndex >= 0) apiPresetNote.Text = ApiProviderPresets.All[apiPreset.SelectedIndex].Note; };
        apiPreset.SelectedIndex = 0; useApiPreset.Click += (_, _) => ApplyApiPreset();
        apiImageDetail.Items.AddRange(["图片精度：服务默认", "图片精度：高清", "图片精度：低清", "图片精度：自动"]);
        checkApiVision.Click += async (_, _) => { if (apiVisionCheck is not null) RequestCancellation(apiVisionCheck); else await CheckApiVision(); };
        checkGoogleTranslate.Click += async (_, _) => { if (googleTranslationCheck is not null) RequestCancellation(googleTranslationCheck); else await CheckGoogleTranslate(); };
        showGoogleBrowser.Click += async (_, _) => await ShowGoogleBrowser();
        target.TextChanged += (_, _) => { if (googleTranslationCheck is null && googleTestResult.Text.Length > 0) googleTestResult.Text = "目标语言已变更，请重新测试谷歌翻译。"; };
        captureMode.Items.AddRange(["VR 左眼", "桌面主显示器"]);
        selectionDistance.Name = "selectionDistance";
        spatialSelection.CheckedChanged += (_, _) => UpdateSelectionHelp();
    }
}
