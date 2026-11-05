using System.Reflection;
using SteamVrTranslator;

public static class UiLayoutTests
{
    public static void Run()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                WindowChromeTests.Run();
                using var form = new MainForm(true);
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-20000, -20000);
                form.ShowInTaskbar = false;
                try
                {
                    form.Show();
                    Settle(form);
                    string output = Path.Combine(AppContext.BaseDirectory, "artifacts");
                    Directory.CreateDirectory(output);
                    var tabs = Find(form).OfType<TabControl>().Single();
                    Check(tabs.TabPages.Count == 4, "Workspace, AI, capture/input and subtitle pages must all exist");
                    Check(!form.HasPendingAiSettings, "Loaded settings should be applied");

                    Navigate(form, tabs, "翻译工作台", 0);
                    CheckWorkspaceActions(form);
                    Render(form, output, "desktop-preview.png");

                    Navigate(form, tabs, "AI 服务", 1);
                    var aiScroll = PageScroll(tabs, 1);
                    var mode = Named<ComboBox>(form, "processingMode");
                    var backend = Named<ComboBox>(form, "translationBackend");
                    var preset = Named<ComboBox>(form, "apiPreset");
                    var detail = Named<ComboBox>(form, "apiImageDetail");
                    var codexPath = Field<TextBox>(form, "codexPath");
                    var recognize = Field<Button>(form, "recognizeButton");
                    var showSource = Field<CheckBox>(form, "showSource");
                    Check(mode.Items.Count == 4 && mode.GetItemText(mode.Items[2]) == "手写截图直译（AI）"
                        && mode.GetItemText(mode.Items[3]) == "本地日语手写 OCR → 翻译",
                        "AI handwriting and offline Japanese OCR are independent processing options");
                    Check(mode.SelectedIndex == 0 && recognize.Text == "仅截屏识别",
                        "Default local OCR keeps the existing recognition button behavior");
                    Check(backend.SelectedIndex == 0 && codexPath.Visible && codexPath.Enabled,
                        "Codex configuration is visible for the loaded Codex backend");
                    Check(!preset.Visible && !detail.Visible && !preset.Enabled,
                        "Unselected API configuration is hidden and disabled");
                    Render(form, output, "settings-preview.png");

                    int savedMode = mode.SelectedIndex;
                    mode.SelectedIndex = savedMode == 0 ? 1 : 0;
                    Check(form.HasPendingAiSettings, "An unapplied processing mode must prevent sending");
                    Settle(form);
                    Render(form, output, "settings-vision-preview.png");
                    mode.SelectedIndex = savedMode;
                    Check(!form.HasPendingAiSettings, "Restoring the applied processing mode clears the dirty guard");

                    mode.SelectedIndex = 2;
                    Settle(form);
                    Check(form.HasPendingAiSettings, "An unapplied handwriting mode must prevent AI requests");
                    Check(!showSource.Enabled, "Direct handwriting translation only returns a translation");
                    Check(recognize.Text == "仅截屏识别", "A handwriting draft must not change the active local recognition action");
                    CheckHandwritingDisclosure(form, "Codex");
                    aiScroll.AutoScrollPosition = Point.Empty;
                    Render(form, output, "settings-handwriting-preview.png");
                    backend.SelectedIndex = 1;
                    Settle(form);
                    CheckHandwritingDisclosure(form, "API");
                    Check(!showSource.Enabled && form.HasPendingAiSettings, "Direct API handwriting keeps the unsaved guard");
                    Check(recognize.Text == "仅截屏识别", "Changing a draft AI provider does not enable AI-only recognition");
                    backend.SelectedIndex = 0;
                    mode.SelectedIndex = savedMode;
                    Check(!form.HasPendingAiSettings, "Restoring the OCR settings clears a handwriting draft");
                    CheckAppliedHandwritingMode(form, mode, recognize, showSource);
                    CheckAppliedLocalJapaneseMode(form, mode, recognize, showSource);
                    mode.SelectedIndex = 3;
                    Settle(form);
                    aiScroll.AutoScrollPosition = Point.Empty;
                    Render(form, output, "settings-local-japanese-preview.png");

                    backend.SelectedIndex = 1;
                    mode.SelectedIndex = 1;
                    Settle(form);
                    Check(preset.Visible && detail.Visible && preset.Enabled && detail.Enabled,
                        "API configuration becomes visible and usable when selected");
                    Check(!codexPath.Visible && !codexPath.Enabled, "Codex configuration is hidden for the API backend");
                    Check(preset.Width > 0 && detail.Width > 0 && backend.Width > 0,
                        "Provider controls retain usable dimensions");
                    aiScroll.AutoScrollPosition = Point.Empty;
                    Settle(form);
                    Render(form, output, "settings-api-preview.png");
                    backend.SelectedIndex = 3;
                    mode.SelectedIndex = 2;
                    Settle(form);
                    var creditAddress = Named<TextBox>(form, "creditServerUrl");
                    var creditAccount = Named<Button>(form, "creditAccountButton");
                    Check(creditAddress.Visible && creditAccount.Visible && creditAddress.Width > 250,
                        "The credit service exposes a readable server address and account action");
                    Check(!preset.Visible && !codexPath.Visible,
                        "Credit service settings must not imply that local API credentials pay for server usage");
                    aiScroll.AutoScrollPosition = Point.Empty;
                    Render(form, output, "settings-credits-preview.png");
                    backend.SelectedIndex = 1;
                    mode.SelectedIndex = 1;
                    aiScroll.AutoScrollPosition = new Point(0, aiScroll.DisplayRectangle.Height);
                    Settle(form);
                    Render(form, output, "settings-bottom-preview.png");
                    backend.SelectedIndex = 0;
                    mode.SelectedIndex = savedMode;
                    Check(!form.HasPendingAiSettings, "Restoring the loaded backend and mode clears all test drafts");

                    Navigate(form, tabs, "截图与按键", 2);
                    PageScroll(tabs, 2).AutoScrollPosition = Point.Empty;
                    Render(form, output, "settings-capture-preview.png");
                    Navigate(form, tabs, "字幕显示", 3);
                    PageScroll(tabs, 3).AutoScrollPosition = Point.Empty;
                    Render(form, output, "settings-subtitle-preview.png");

                    // Exercise the compact window without assuming an exact minimum size.
                    form.Size = new Size(1120, 780);
                    Navigate(form, tabs, "翻译工作台", 0);
                    CheckWorkspaceActions(form);
                    Render(form, output, "desktop-small-preview.png");
                    Navigate(form, tabs, "AI 服务", 1);
                    backend.SelectedIndex = 1;
                    mode.SelectedIndex = 1;
                    aiScroll.AutoScrollPosition = Point.Empty;
                    Render(form, output, "settings-api-small-preview.png");
                    mode.SelectedIndex = 2;
                    Render(form, output, "settings-handwriting-small-preview.png");
                    mode.SelectedIndex = 3;
                    Render(form, output, "settings-local-japanese-small-preview.png");
                    mode.SelectedIndex = 1;
                    Navigate(form, tabs, "截图与按键", 2);
                    Render(form, output, "settings-capture-small-preview.png");
                    Navigate(form, tabs, "字幕显示", 3);
                    Render(form, output, "settings-subtitle-small-preview.png");
                }
                finally { form.Close(); }
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) throw new Exception("UI layout smoke failed", failure);
    }

    private static void Navigate(MainForm form, TabControl tabs, string label, int index)
    {
        var button = Find(form).OfType<Button>().Single(control => control.Text == label);
        Check(button.Visible && button.Enabled, "Sidebar navigation must remain available: " + label);
        CheckFullyVisible(button);
        button.PerformClick();
        Settle(form);
        Check(tabs.SelectedIndex == index, "Sidebar navigation selects the intended page: " + label);
    }

    private static Panel PageScroll(TabControl tabs, int index)
    {
        var panel = Named<Panel>(tabs.TabPages[index], "pageScroll" + index);
        Check(panel.AutoScroll && panel.ClientSize.Width > 0 && panel.ClientSize.Height > 0,
            "Settings page has a usable scroll viewport");
        return panel;
    }

    private static void CheckWorkspaceActions(MainForm form)
    {
        var translate = Field<Button>(form, "translateButton");
        var recognize = Field<Button>(form, "recognizeButton");
        foreach (var button in new[] { translate, recognize })
        {
            Check(button.Visible && button.Enabled, "Main capture actions remain available while idle");
            CheckFullyVisible(button);
        }
        Check(!translate.RectangleToScreen(translate.ClientRectangle).IntersectsWith(
            recognize.RectangleToScreen(recognize.ClientRectangle)), "Main capture actions do not overlap");
    }

    private static void CheckHandwritingDisclosure(MainForm form, string backend)
    {
        string note = Field<Label>(form, "dataNote").Text;
        Check(note.Contains("截图", StringComparison.Ordinal) && note.Contains(backend, StringComparison.Ordinal),
            "Handwriting disclosure identifies the screenshot destination: " + backend);
        Check(note.Contains("一次请求直接返回译文", StringComparison.Ordinal) && note.Contains("核对原文", StringComparison.Ordinal),
            "Handwriting disclosure separates direct translation from inspecting the source");
        Check(note.Contains("仅识别", StringComparison.Ordinal) && note.Contains("AI", StringComparison.Ordinal),
            "Handwriting disclosure explicitly says recognition-only also uses AI");
    }

    private static void CheckAppliedLocalJapaneseMode(MainForm form, ComboBox mode, Button recognize, CheckBox showSource)
    {
        var settings = (AppSettings)typeof(MainForm).GetField("settings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
        var load = typeof(MainForm).GetMethod("LoadSettings", BindingFlags.Instance | BindingFlags.NonPublic)!;
        string originalMode = settings.ProcessingMode;
        try
        {
            settings.ProcessingMode = "LocalJapanese";
            load.Invoke(form, null);
            Settle(form);
            Check(mode.SelectedIndex == 3 && !form.HasPendingAiSettings, "Saved local Japanese mode maps to its own option");
            Check(recognize.Text == "仅识别日语（本地）" && showSource.Enabled, "Local-only recognition is explicit and source text is available");
            string note = Field<Label>(form, "dataNote").Text;
            Check(note.Contains("CPU") && note.Contains("图片不会发送") && note.Contains("Codex")
                && note.Contains("不会自动改用云端"), "Local OCR disclosure separates image recognition from translation");
            Check(Field<Control>(form, "localOcrSettingsCard").Visible, "Offline engine information is visible for local Japanese mode");
            mode.SelectedIndex = 2;
            Settle(form);
            Check(form.HasPendingAiSettings && recognize.Text == "仅识别日语（本地）", "An AI draft cannot silently replace the saved offline action");
            Check(!Field<Control>(form, "localOcrSettingsCard").Visible, "Offline engine card hides for AI handwriting");
        }
        finally
        {
            settings.ProcessingMode = originalMode;
            load.Invoke(form, null);
            Settle(form);
        }
    }

    private static void CheckAppliedHandwritingMode(MainForm form, ComboBox mode, Button recognize, CheckBox showSource)
    {
        // Emulate a previously saved setting in this demo form only. Never invoke ApplySettings,
        // click capture, or read/write the real user's settings directory.
        var settings = (AppSettings)typeof(MainForm).GetField("settings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
        var load = typeof(MainForm).GetMethod("LoadSettings", BindingFlags.Instance | BindingFlags.NonPublic)!;
        string originalMode = settings.ProcessingMode;
        try
        {
            settings.ProcessingMode = "Handwriting";
            load.Invoke(form, null);
            Settle(form);
            Check(mode.SelectedIndex == 2 && !form.HasPendingAiSettings, "Applied handwriting settings map back to the third option");
            Check(recognize.Text == "仅识别手写（AI）", "Only an applied handwriting setting changes the active recognition action");
            Check(!showSource.Enabled, "Applied handwriting translation does not promise a source transcript");
            mode.SelectedIndex = 0;
            Check(form.HasPendingAiSettings, "A local-OCR draft remains unapplied while handwriting is active");
            Check(recognize.Text == "仅识别手写（AI）", "An unsaved local-OCR draft must not disguise the active AI recognition action");
            mode.SelectedIndex = 1;
            Check(form.HasPendingAiSettings && !showSource.Enabled, "Direct-image draft remains distinct from applied handwriting mode");
            Check(recognize.Text == "仅识别手写（AI）", "A direct-image draft cannot change the applied recognition action");
            mode.SelectedIndex = 2;
            Check(!form.HasPendingAiSettings && !showSource.Enabled, "Restoring direct handwriting translation clears the guard");
        }
        finally
        {
            settings.ProcessingMode = originalMode;
            load.Invoke(form, null);
            Settle(form);
        }
        Check(mode.SelectedIndex == 0 && recognize.Text == "仅截屏识别" && !form.HasPendingAiSettings,
            "Restoring demo defaults returns to local recognition without persisting changes");
    }

    private static void CheckFullyVisible(Control control)
    {
        Check(control.ClientSize.Width > 0 && control.ClientSize.Height > 0, "Interactive control has positive dimensions");
        Rectangle bounds = control.RectangleToScreen(control.ClientRectangle);
        for (Control? parent = control.Parent; parent is not null; parent = parent.Parent)
            Check(parent.RectangleToScreen(parent.ClientRectangle).Contains(bounds),
                "Interactive control is not clipped by its container: " + control.Text);
    }

    private static void Render(MainForm form, string output, string filename)
    {
        Settle(form);
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
        bitmap.Save(Path.Combine(output, filename));
    }

    private static void Settle(MainForm form)
    {
        form.PerformLayout();
        Application.DoEvents();
        form.PerformLayout();
    }

    private static T Named<T>(Control root, string name) where T : Control =>
        Find(root).OfType<T>().Single(control => control.Name == name);

    private static T Field<T>(MainForm form, string name) where T : Control =>
        (T)typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static IEnumerable<Control> Find(Control root)
    {
        foreach (Control control in root.Controls)
        {
            yield return control;
            foreach (var child in Find(control)) yield return child;
        }
    }
}
