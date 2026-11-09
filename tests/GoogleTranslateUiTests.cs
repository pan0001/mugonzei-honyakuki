using System.Reflection;
using SteamVrTranslator;

public static class GoogleTranslateUiTests
{
    public static void Run()
    {
        CheckModeValidation();
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new MainForm(true)
                {
                    StartPosition = FormStartPosition.Manual,
                    Location = new Point(-20000, -20000),
                    ShowInTaskbar = false
                };
                try
                {
                    form.Show();
                    var tabs = Field<TabControl>(form, "tabs");
                    tabs.SelectedIndex = 1;
                    Settle(form);
                    var backend = Field<ComboBox>(form, "backend");
                    var mode = Field<ComboBox>(form, "processingMode");
                    var showSource = Field<CheckBox>(form, "showSource");
                    var recognize = Field<Button>(form, "recognizeButton");
                    Check(backend.GetItemText(backend.Items[2]) == "Google Translate（后台网页）"
                        && backend.Items.Cast<object>().Any(item => backend.GetItemText(item)?.StartsWith("Google 搜索 AI 模式", StringComparison.Ordinal) == true),
                        "The legacy text-only Google Translate selection stays distinct from Google Search AI Mode");
                    Check(!form.HasPendingAiSettings, "Demo defaults begin without unsaved AI changes");

                    backend.SelectedIndex = 2;
                    Settle(form);
                    CheckGoogleCard(form);
                    Check(form.HasPendingAiSettings, "An unsaved Google selection must not silently become the active backend");
                    Check(mode.SelectedIndex == 0 && showSource.Enabled && recognize.Text == "仅截屏识别",
                        "Google's local OCR draft preserves local recognition and source display");
                    mode.SelectedIndex = 3;
                    Settle(form);
                    CheckGoogleCard(form);
                    Check(showSource.Enabled && form.HasPendingAiSettings,
                        "Local Japanese OCR can supply source text to Google without applying the draft");
                    Check(recognize.Text == "仅截屏识别", "A LocalJapanese draft must not change the saved recognition action");
                    mode.SelectedIndex = 0;
                    SavePreview(form, "settings-google-preview.png");

                    backend.SelectedIndex = 0;
                    Settle(form);
                    Check(!Field<Control>(form, "googleSettingsCard").Visible && !form.HasPendingAiSettings,
                        "Restoring the original backend hides Google and clears the unsaved guard");
                    CheckAppliedSettings(form, backend, mode, recognize, showSource);
                }
                finally { form.Close(); }
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Exception("Google Translate UI checks failed", failure);
        Console.WriteLine("PASS Google Translate UI visibility, local OCR modes, saved/draft mapping and image-mode rejection");
    }

    private static void CheckModeValidation()
    {
        foreach (string mode in new[] { "Ocr", "LocalJapanese" })
            MainForm.ValidateBackendMode(new AppSettings { TranslationBackend = "GoogleTranslate", ProcessingMode = mode });
        foreach (string mode in new[] { "Image", "Handwriting" })
        {
            try
            {
                MainForm.ValidateBackendMode(new AppSettings { TranslationBackend = "GoogleTranslate", ProcessingMode = mode });
            }
            catch (InvalidOperationException error)
            {
                Check(!string.IsNullOrWhiteSpace(error.Message), "Incompatible image modes explain why saving is refused");
                continue;
            }
            throw new Exception("Google Translate must reject image-dependent mode: " + mode);
        }
        foreach (string backend in new[] { "Codex", "OpenAI" })
            foreach (string mode in new[] { "Ocr", "Image", "Handwriting", "LocalJapanese" })
                MainForm.ValidateBackendMode(new AppSettings { TranslationBackend = backend, ProcessingMode = mode });
    }

    private static void CheckAppliedSettings(MainForm form, ComboBox backend, ComboBox mode, Button recognize, CheckBox showSource)
    {
        var settings = Field<AppSettings>(form, "settings");
        var load = typeof(MainForm).GetMethod("LoadSettings", BindingFlags.Instance | BindingFlags.NonPublic)!;
        string oldBackend = settings.TranslationBackend, oldMode = settings.ProcessingMode;
        try
        {
            settings.TranslationBackend = "GoogleTranslate";
            foreach (var pair in new[] { (Mode: "Ocr", Index: 0, Button: "仅截屏识别"), (Mode: "LocalJapanese", Index: 3, Button: "仅识别日语（本地）") })
            {
                settings.ProcessingMode = pair.Mode;
                load.Invoke(form, null);
                Settle(form);
                Check(backend.SelectedIndex == 2 && mode.SelectedIndex == pair.Index && !form.HasPendingAiSettings,
                    "Loaded Google settings map back to their own backend and local mode: " + pair.Mode);
                CheckGoogleCard(form);
                Check(recognize.Text == pair.Button && showSource.Enabled,
                    "Applied local recognition remains independent of the text translation backend: " + pair.Mode);
                backend.SelectedIndex = 1;
                Check(form.HasPendingAiSettings, "Changing the loaded Google backend remains an unapplied draft");
                backend.SelectedIndex = 2;
                Check(!form.HasPendingAiSettings, "Restoring the loaded Google backend clears the draft");
            }
        }
        finally
        {
            settings.TranslationBackend = oldBackend;
            settings.ProcessingMode = oldMode;
            load.Invoke(form, null);
            Settle(form);
        }
    }

    private static void CheckGoogleCard(MainForm form)
    {
        Check(Field<Control>(form, "googleSettingsCard").Visible, "Google information and connection test have their own visible card");
        Check(!Field<Control>(form, "codexSettingsCard").Visible && !Field<Control>(form, "apiSettingsCard").Visible,
            "Google selection hides unrelated Codex and API settings");
        foreach (string name in new[] { "codexPath", "codexModel", "endpoint", "model", "apiKey" })
        {
            var input = Field<Control>(form, name);
            Check(!input.Visible && !input.Enabled, "Google does not request an API key, endpoint or model: " + name);
        }
        var test = Field<Button>(form, "checkGoogleTranslate");
        Check(test.Visible && test.Enabled, "Google has its own test action without starting a request during UI checks");
        var browser = Field<Button>(form, "showGoogleBrowser");
        Check(browser.Visible && browser.Enabled && browser.Text == "查看翻译网页", "Background webpage can be explicitly inspected without starting it during UI checks");
    }

    private static void SavePreview(MainForm form, string filename)
    {
        Settle(form);
        string directory = Path.Combine(AppContext.BaseDirectory, "artifacts");
        Directory.CreateDirectory(directory);
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
        bitmap.Save(Path.Combine(directory, filename));
    }

    private static void Settle(MainForm form)
    {
        form.PerformLayout();
        Application.DoEvents();
        form.PerformLayout();
    }

    private static T Field<T>(MainForm form, string name) =>
        (T)typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
