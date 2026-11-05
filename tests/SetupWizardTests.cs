using System.Reflection;
using System.Text.Json;
using Valve.VR;

namespace SteamVrTranslator;

public static class SetupWizardTests
{
    public static void Run()
    {
        InitialSetupAndLegacySettings();
        CopyPreservesAllSettings();
        SaveValidation();
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                CreditSetupLoginTests.Run();
                StartupAccountRouting();
                FirstRunRecommendedDefaults();
                ReopenedServicesKeepSettings();
                CancelKeepsFirstRun();
                FailedSaveStaysOpen();
                SuccessfulSavePreservesAdvancedSettings();
                RerunCancelPreservesSavedConfiguration();
                SwitchingServicePreservesUnselectedSettings();
                WizardValidationAndLayout();
                MainFormModalGuards();
                ResetRequiresAReleasedController();
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Exception("Setup wizard UI regression failed", failure);
        Console.WriteLine("PASS initial setup detection, legacy/DPAPI preservation, isolated drafts, save failure, validation and wizard layout (no network/VR)");
    }

    private static AppSettings DetailedSettings() => new()
    {
        TranslationBackend = "OpenAI",
        ProcessingMode = "Handwriting",
        CodexExecutablePath = "",
        CodexModel = "fixture-codex-model",
        CodexReasoningEffort = "low",
        ApiBaseUrl = "https://fixture.invalid/v1",
        ApiModel = "fixture-vision-model",
        ApiImageDetail = "High",
        ApiStream = true,
        ApiKey = "synthetic-setup-test-key-not-a-credential",
        TargetLanguage = "日语",
        OcrLanguages = "jpn+eng",
        CaptureMode = "桌面主显示器",
        CropX = .13f,
        CropY = .17f,
        CropWidth = .57f,
        CropHeight = .61f,
        Hotkey = "Ctrl+Shift+F9",
        ControllerEnabled = false,
        SpatialSelectionEnabled = true,
        SelectionDistance = 1.8f,
        OverlayWidth = 1.1f,
        OverlayDistance = 1.7f,
        OverlayVerticalOffset = -.47f,
        DisplaySeconds = 43,
        ShowSource = true
    };

    private static void InitialSetupAndLegacySettings()
    {
        using var fixture = new SettingsFixture();
        Check(SettingsStore.NeedsInitialSetup(fixture.Directory), "A missing settings document starts first-run setup");
        Check(!System.IO.Directory.Exists(fixture.Directory), "Checking first-run status must not create a settings directory");
        var original = DetailedSettings();
        SettingsStore.Save(original, fixture.Directory);
        byte[] before = File.ReadAllBytes(fixture.SettingsPath);
        using (var document = JsonDocument.Parse(before))
        {
            Check(document.RootElement.GetProperty("Version").GetInt32() == 1,
                "The fixture represents the existing Version 1 settings format");
            Check(!System.Text.Encoding.UTF8.GetString(before).Contains(original.ApiKey, StringComparison.Ordinal),
                "The synthetic API key is protected on disk");
        }
        Check(!SettingsStore.NeedsInitialSetup(fixture.Directory),
            "Existing settings skip first-run setup without requiring a new completion marker");
        AssertSettingsEqual(original, SettingsStore.Load(fixture.Directory), "Legacy settings, including the protected key, survive setup detection");
        Check(before.AsSpan().SequenceEqual(File.ReadAllBytes(fixture.SettingsPath)),
            "Detecting or loading existing settings must not rewrite the document");
        File.WriteAllText(fixture.SettingsPath, "invalid legacy JSON");
        Check(!SettingsStore.NeedsInitialSetup(fixture.Directory),
            "An existing but unreadable settings file must not be silently treated as a clean first run");
    }

    private static void CopyPreservesAllSettings()
    {
        var original = DetailedSettings();
        var copy = original.Copy();
        Check(!ReferenceEquals(original, copy), "A settings draft must be a different object");
        AssertSettingsEqual(original, copy, "Copy preserves every setting, including JsonIgnore ApiKey");
        copy.ApiKey = "different-synthetic-key";
        copy.ProcessingMode = "Ocr";
        copy.CropWidth = .2f;
        Check(original.ApiKey != copy.ApiKey && original.ProcessingMode == "Handwriting" && original.CropWidth == .57f,
            "Editing a draft cannot mutate the saved configuration");
    }

    private static void SaveValidation()
    {
        SettingsValidation.ValidateForSave(new AppSettings(), requireServiceConfiguration: true);
        foreach (string mode in new[] { "Ocr", "Image", "Handwriting", "LocalJapanese" })
        {
            var settings = DetailedSettings();
            settings.ProcessingMode = mode;
            SettingsValidation.ValidateForSave(settings, requireServiceConfiguration: true);
        }
        var local = DetailedSettings();
        local.ApiBaseUrl = "http://127.0.0.1:1234/v1";
        local.ApiKey = "";
        SettingsValidation.ValidateForSave(local, requireServiceConfiguration: true);
        local.ApiModel = "";
        SettingsValidation.ValidateForSave(local, requireServiceConfiguration: false);
        Reject(() => SettingsValidation.ValidateForSave(local, requireServiceConfiguration: true),
            "First-run API setup must require a model before completion");
        var missingRemoteKey = DetailedSettings(); missingRemoteKey.ApiKey = "";
        Reject(() => SettingsValidation.ValidateForSave(missingRemoteKey, requireServiceConfiguration: true),
            "Remote API setup must require its key while a local unauthenticated service may omit one");
        foreach (string endpoint in new[] { "not-a-url", "http://remote.invalid/v1", "https://user:password@fixture.invalid/v1", "https://fixture.invalid/v1?key=synthetic" })
        {
            var settings = DetailedSettings(); settings.ApiBaseUrl = endpoint;
            Reject(() => SettingsValidation.ValidateForSave(settings, requireServiceConfiguration: true),
                "Invalid or credential-bearing API destinations must not be saved by the wizard");
        }
        foreach (string mode in new[] { "Ocr", "LocalJapanese" })
            SettingsValidation.ValidateForSave(new AppSettings { TranslationBackend = "GoogleTranslate", ProcessingMode = mode }, true);
        foreach (string mode in new[] { "Image", "Handwriting" })
            Reject(() => SettingsValidation.ValidateForSave(new AppSettings { TranslationBackend = "GoogleTranslate", ProcessingMode = mode }, true),
                "Google Translate cannot be configured as an image recognizer");
        foreach (string language in new[] { "简体中文", "日语", "en", "fr" })
            SettingsValidation.ValidateForSave(new AppSettings { TranslationBackend = "GoogleTranslate", TargetLanguage = language }, true);
        foreach (string language in new[] { "auto", "not a language code" })
            Reject(() => SettingsValidation.ValidateForSave(new AppSettings { TranslationBackend = "GoogleTranslate", TargetLanguage = language }, true),
                "Google target language must be valid before completing setup");
        foreach (Action<AppSettings> mutate in new Action<AppSettings>[]
        {
            s => s.TargetLanguage = " ",
            s => s.Hotkey = "Ctrl+Alt",
            s => s.Hotkey = "Ctrl+Alt+Right",
            s => { s.CropX = .8f; s.CropWidth = .3f; },
            s => s.OverlayDistance = float.NaN,
            s => s.OcrLanguages = "../jpn",
            s => s.CodexReasoningEffort = "unknown"
        })
        {
            var settings = new AppSettings(); mutate(settings);
            Reject(() => SettingsValidation.ValidateForSave(settings), "Invalid capture, hotkey or settings values must be rejected before saving");
        }
    }

    private static void CancelKeepsFirstRun()
    {
        using var fixture = new SettingsFixture();
        int saves = 0;
        using (var wizard = Create(new AppSettings(), _ => saves++))
        {
            Show(wizard);
            Check(wizard.CurrentStep == 0, "First run starts at the first setup step");
            wizard.CancelSetup();
            Check(wizard.DialogResult == DialogResult.Cancel, "Cancel returns an explicit canceled result");
        }
        Check(saves == 0 && SettingsStore.NeedsInitialSetup(fixture.Directory)
            && !System.IO.Directory.Exists(fixture.Directory), "Canceling setup does not mark it complete or write files");
        using var again = Create(new AppSettings(), _ => saves++);
        Show(again);
        Check(again.CurrentStep == 0 && again.TryAdvance(), "The next launch restarts a usable first-run wizard");
        again.CancelSetup();
        Check(saves == 0 && SettingsStore.NeedsInitialSetup(fixture.Directory),
            "Canceling after advancing a step also leaves first-run setup pending");
    }

    private static void StartupAccountRouting()
    {
        var settings = new AppSettings { TranslationBackend = "Credits" };
        bool Needs(Func<string, CreditSession?> load) => SteamVrTranslator.Program.NeedsAccountSignIn(settings, load);
        Check(Needs(_ => null) && Needs(_ => new CreditSession("t", "tester", DateTimeOffset.UtcNow.AddMinutes(-1), null)),
            "Old account setups without a usable session open the standalone sign-in step");
        Check(!Needs(_ => new CreditSession("t", "tester", DateTimeOffset.UtcNow.AddMinutes(15), null)), "An existing login continues to the workspace");
        settings.TranslationBackend = "Codex";
        Check(!Needs(_ => throw new Exception("Must not read account state")), "Non-account setups never ask for an account");
    }

    internal static void SubmitFixtureLogin(SetupWizardForm wizard)
    {
        Named<TextBox>(wizard, "wizardLoginUsername").Text = "fixture-user";
        Named<TextBox>(wizard, "wizardLoginPassword").Text = "fixture-password";
        Named<Button>(wizard, "wizardLoginSubmit").PerformClick();
        var limit = Environment.TickCount64 + 1500;
        while (wizard.CurrentStep == 1 && Environment.TickCount64 < limit) { Application.DoEvents(); Thread.Sleep(2); }
        Check(wizard.CurrentStep == 2, "Successful sign-in automatically opens the mode picker");
    }

    private static void FirstRunRecommendedDefaults()
    {
        var original = new AppSettings();
        var before = original.Copy();
        AppSettings? saved = null;
        using var wizard = Create(original, draft => saved = draft);
        Show(wizard);
        var draft = wizard.ReadSettingsDraft();
        Check(draft.TranslationBackend == "Credits" && draft.CreditServerUrl == "https://api.mugonzei.top"
            && draft.ProcessingMode == "Handwriting", "First run recommends the public account service and AI handwriting recognition");
        Check(Named<RadioButton>(wizard, "wizardServiceCredits").Checked,
            "The recommended account card is initially selected");
        Check(!Named<ComboBox>(wizard, "wizardProcessingMode").Visible
            && !Named<TextBox>(wizard, "wizardTargetLanguage").Visible,
            "Service selection does not expose the second step's recognition fields");
        AssertSettingsEqual(before, original, "First-run recommendations never mutate the caller's settings");
        Check(wizard.TryAdvance() && wizard.CurrentStep == 1, "Account service opens its own login step");
        SavePreview(wizard, "setup-credits-preview.png");
        Check(!wizard.TryAdvance() && !wizard.TryFinish() && saved is null, "Account sign-in cannot be skipped");
        Check(!Named<ComboBox>(wizard, "wizardProcessingMode").Visible && !Named<TextBox>(wizard, "wizardTargetLanguage").Visible
            && !Named<Button>(wizard, "wizardNext").Visible && Named<Button>(wizard, "wizardLoginSubmit").Visible
            && !Descendants(wizard).Any(c => c.Visible && (c.Text.Contains("兑换") || c.Text.Contains("充值"))),
            "Sign-in exposes no billing, recognition, or second submit control");
        SubmitFixtureLogin(wizard);
        Check(Named<RadioButton>(wizard, "wizardDesktop").Visible && !Named<CheckBox>(wizard, "wizardControllerEnabled").Visible,
            "Desktop mode shows the hotkey without headset controls");
        Named<RadioButton>(wizard, "wizardSteamVr").Checked = true;
        Check(!wizard.ReadSettingsDraft().DesktopMode && Named<CheckBox>(wizard, "wizardControllerEnabled").Visible, "SteamVR mode restores controller options");
        Named<RadioButton>(wizard, "wizardDesktop").Checked = true;
        SavePreview(wizard, "setup-screen-preview.png");
        wizard.GoBack();
        Check(wizard.CurrentStep == 0 && wizard.TryAdvance() && wizard.CurrentStep == 2,
            "Returning to the same authenticated service does not ask for credentials again");
        Check(wizard.TryFinish() && saved is { DesktopMode: true }, "Save persists the explicitly selected desktop runtime");
        Check(saved!.TranslationBackend == "Credits" && saved.CreditServerUrl == draft.CreditServerUrl
            && saved.ProcessingMode == "Handwriting", "The saved first-run draft retains the recommended destination and mode");
        AssertSettingsEqual(before, original, "Finishing initial setup leaves the caller's original settings intact");
    }

    private static void ReopenedServicesKeepSettings()
    {
        foreach (string service in new[] { "Credits", "OpenAI", "Codex", "GoogleTranslate" })
        {
            var initial = DetailedSettings();
            initial.TranslationBackend = service;
            initial.CreditServerUrl = "https://custom-credit-fixture.invalid/service/";
            if (service == "GoogleTranslate") initial.ProcessingMode = "LocalJapanese";
            AppSettings? saved = null;
            using var wizard = Create(initial, draft => saved = draft, firstRun: false);
            Show(wizard);
            AssertSettingsEqual(initial, wizard.ReadSettingsDraft(), "Reopening preserves every provider's original settings: " + service);
            Check(wizard.TryAdvance(), "Reopened settings reach their own service configuration");
            if (service == "Credits")
            {
                Check(Named<Label>(wizard, "wizardCustomLoginOrigin").Text.Contains(initial.CreditServerUrl)
                    && !Named<Control>(wizard, "wizardLoginRegister").Visible, "A custom service is identified and never offers official registration");
                SubmitFixtureLogin(wizard);
            }
            else Check(wizard.TryAdvance(), "Other services do not require account sign-in");
            Check(wizard.TryFinish() && saved is not null, "Reopened setup persists the deliberate choice");
            AssertSettingsEqual(initial, saved!, "Saving reopened setup preserves all fields, including hidden credentials: " + service);
        }
    }

    private static void FailedSaveStaysOpen()
    {
        using var fixture = new SettingsFixture();
        int saves = 0;
        using var wizard = Create(new AppSettings { TranslationBackend = "Codex" }, _ => { saves++; throw new IOException("Synthetic write denied"); }, firstRun: false);
        Show(wizard);
        AdvanceToFinish(wizard);
        Check(!wizard.TryFinish(), "A failed persistence callback cannot complete setup");
        Check(saves == 1 && wizard.Visible && !wizard.IsDisposed && wizard.DialogResult == DialogResult.None,
            "Save failure keeps the wizard open for correction or cancellation");
        Check(wizard.CurrentStep == 2 && !string.IsNullOrWhiteSpace(wizard.LastError),
            "Save failure stays on the confirmation step and explains the error inline");
        Check(SettingsStore.NeedsInitialSetup(fixture.Directory) && !File.Exists(fixture.SettingsPath),
            "A failed save never creates a completion record");
        SavePreview(wizard, "setup-save-error-preview.png");
        wizard.CancelSetup();
    }

    private static void SuccessfulSavePreservesAdvancedSettings()
    {
        using var fixture = new SettingsFixture();
        var original = DetailedSettings();
        AppSettings? saved = null;
        int saves = 0;
        using var wizard = Create(original, draft =>
        {
            saves++;
            saved = draft;
            SettingsStore.Save(draft, fixture.Directory);
        }, firstRun: false);
        Show(wizard);
        AssertSettingsEqual(original, wizard.ReadSettingsDraft(), "Opening setup preserves advanced fields that the wizard does not expose");
        AdvanceToFinish(wizard);
        Check(wizard.TryFinish(), "Valid settings complete setup only after the save callback succeeds");
        Check(saves == 1 && wizard.DialogResult == DialogResult.OK && saved is not null && !ReferenceEquals(original, saved),
            "The caller receives one independent saved draft and an OK result");
        Check(!SettingsStore.NeedsInitialSetup(fixture.Directory), "A successful saved document completes first-run setup");
        AssertSettingsEqual(original, SettingsStore.Load(fixture.Directory), "Finishing the wizard preserves advanced settings and the protected key");
    }

    private static void RerunCancelPreservesSavedConfiguration()
    {
        using var fixture = new SettingsFixture();
        var initial = DetailedSettings();
        SettingsStore.Save(initial, fixture.Directory);
        byte[] before = File.ReadAllBytes(fixture.SettingsPath);
        int saves = 0;
        using var wizard = Create(initial, _ => saves++, firstRun: false);
        Show(wizard);
        Check(wizard.TryAdvance() && wizard.CurrentStep == 1,
            "Reopened setup reaches its API settings before editing the draft");
        Named<TextBox>(wizard, "wizardApiModel").Text = "draft-only-model";
        Named<TextBox>(wizard, "wizardApiKey").Text = "draft-only-synthetic-key";
        var draft = wizard.ReadSettingsDraft();
        Check(draft.ApiModel == "draft-only-model" && draft.ApiKey == "draft-only-synthetic-key",
            "Reopened setup exposes a mutable draft");
        AssertSettingsEqual(DetailedSettings(), initial, "Editing reopened setup leaves the source settings untouched");
        wizard.CancelSetup();
        Check(saves == 0 && before.AsSpan().SequenceEqual(File.ReadAllBytes(fixture.SettingsPath)),
            "Canceling reopened setup does not rewrite or replace the saved configuration");
        AssertSettingsEqual(initial, SettingsStore.Load(fixture.Directory), "Cancel preserves the existing DPAPI-protected API key");
    }

    private static void SwitchingServicePreservesUnselectedSettings()
    {
        // Exercise each invalid field independently so switching providers cannot
        // merely hide an error while persisting the abandoned provider's draft.
        foreach (string invalidField in new[] { "api-address", "api-model", "codex-path", "codex-model" })
        {
            using var fixture = new SettingsFixture();
            bool startsWithApi = invalidField.StartsWith("api-", StringComparison.Ordinal);
            var initial = DetailedSettings();
            initial.TranslationBackend = startsWithApi ? "OpenAI" : "Codex";
            var original = initial.Copy();
            AppSettings? saved = null;
            int saves = 0;
            using var wizard = Create(initial, draft =>
            {
                SettingsStore.Save(draft, fixture.Directory);
                saved = draft;
                saves++;
            }, firstRun: false);
            Show(wizard);
            Check(wizard.TryAdvance() && wizard.CurrentStep == 1,
                "A valid provider reaches its service configuration page");
            if (startsWithApi)
            {
                if (invalidField == "api-address") Named<TextBox>(wizard, "wizardApiEndpoint").Text = "";
                else Named<TextBox>(wizard, "wizardApiModel").Text = new string('m', 257);
                Named<TextBox>(wizard, "wizardApiKey").Text = "abandoned-synthetic-key";
                Named<ComboBox>(wizard, "wizardApiImageDetail").SelectedIndex = 2;
                Named<CheckBox>(wizard, "wizardApiStream").Checked = false;
            }
            else
            {
                if (invalidField == "codex-path") Named<TextBox>(wizard, "wizardCodexPath").Text = new string('p', 4097);
                else Named<TextBox>(wizard, "wizardCodexModel").Text = new string('m', 257);
                Named<ComboBox>(wizard, "wizardCodexReasoningEffort").SelectedIndex = 3;
            }
            Check(!wizard.TryAdvance() && wizard.CurrentStep == 1 && wizard.LastError.Length > 0,
                "The invalid active provider must be rejected before switching: " + invalidField);
            wizard.GoBack();
            Check(wizard.CurrentStep == 0, "Back returns to provider selection after a validation error");
            Named<RadioButton>(wizard, startsWithApi ? "wizardServiceCodex" : "wizardServiceApi").Checked = true;
            Check(wizard.TryAdvance() && wizard.CurrentStep == 1,
                "The replacement provider reaches service configuration");
            var expected = original.Copy();
            expected.TranslationBackend = startsWithApi ? "Codex" : "OpenAI";
            if (startsWithApi)
            {
                Named<TextBox>(wizard, "wizardCodexModel").Text = expected.CodexModel = "chosen-codex-model";
                Named<ComboBox>(wizard, "wizardCodexReasoningEffort").SelectedIndex = 2;
                expected.CodexReasoningEffort = "medium";
            }
            else Named<TextBox>(wizard, "wizardApiModel").Text = expected.ApiModel = "chosen-api-model";
            Check(wizard.TryAdvance() && wizard.CurrentStep == 2 && wizard.TryFinish(),
                "An abandoned hidden provider draft cannot block saving the selected provider: " + invalidField);
            Check(saves == 1 && saved is not null && wizard.DialogResult == DialogResult.OK,
                "Switching providers saves exactly once after validation succeeds");
            AssertSettingsEqual(expected, saved!,
                "Only the selected provider's fields are saved; the unselected provider retains its initial values");
            AssertSettingsEqual(expected, SettingsStore.Load(fixture.Directory),
                "The saved document retains unselected provider credentials and settings");
            AssertSettingsEqual(original, initial, "Switching and saving does not mutate the original settings object");
        }
    }

    private static void WizardValidationAndLayout()
    {
        int saves = 0;
        using var wizard = Create(new AppSettings(), _ => saves++);
        Show(wizard);
        CheckServiceCards(wizard);
        SavePreview(wizard, "setup-welcome-preview.png");
        wizard.Size = wizard.MinimumSize;
        CheckServiceCards(wizard);
        SavePreview(wizard, "setup-welcome-small-preview.png");
        wizard.Size = new Size(940, 800);
        Named<RadioButton>(wizard, "wizardServiceGoogle").Checked = true;
        Check(wizard.ReadSettingsDraft().ProcessingMode == "Ocr", "Choosing Google replaces an incompatible AI mode with local OCR");
        Check(wizard.TryAdvance() && wizard.CurrentStep == 1, "Google reaches recognition options on the service step");
        foreach (int invalidMode in new[] { 1, 2 })
        {
            Named<ComboBox>(wizard, "wizardProcessingMode").SelectedIndex = invalidMode;
            Check(!wizard.TryAdvance() && wizard.CurrentStep == 1 && wizard.LastError.Length > 0,
                "Manually selecting an AI image mode cannot pass Google service setup");
        }
        Named<ComboBox>(wizard, "wizardProcessingMode").SelectedIndex = 0;
        SavePreview(wizard, "setup-google-preview.png");
        wizard.GoBack();
        Named<RadioButton>(wizard, "wizardServiceCodex").Checked = true;
        Check(wizard.TryAdvance() && wizard.CurrentStep == 1, "Valid provider/mode settings reach service setup");
        SavePreview(wizard, "setup-codex-preview.png");
        Check(wizard.TryAdvance() && wizard.CurrentStep == 2, "Codex auto-detection requires no model call to advance");
        SavePreview(wizard, "setup-confirm-preview.png");
        wizard.CancelSetup();

        using var api = Create(DetailedSettings(), _ => saves++, firstRun: false);
        Show(api);
        Check(api.TryAdvance() && api.CurrentStep == 1, "Configured API settings reach the service step");
        var key = Named<TextBox>(api, "wizardApiKey");
        Check(key.UseSystemPasswordChar || key.PasswordChar != '\0', "The setup API key stays masked in screenshots and on screen");
        Named<TextBox>(api, "wizardApiModel").Text = "";
        Check(!api.TryAdvance() && api.CurrentStep == 1 && api.LastError.Length > 0,
            "An empty API model cannot pass service setup");
        Named<TextBox>(api, "wizardApiModel").Text = "fixture-vision-model";
        Check(api.TryAdvance(), "A corrected API model can pass service setup");
        api.GoBack();
        SavePreview(api, "setup-api-preview.png");
        if (api.MinimumSize.Width > 0 && api.MinimumSize.Height > 0) api.Size = api.MinimumSize;
        SavePreview(api, "setup-api-small-preview.png");
        api.CancelSetup();
        Check(saves == 0, "Navigation and validation never persist settings or call services");
    }

    private static void CheckServiceCards(SetupWizardForm wizard)
    {
        string[] names = ["wizardServiceCredits", "wizardServiceApi", "wizardServiceCodex", "wizardServiceGoogle"];
        string[] labels = ["账号积分服务（推荐）", "使用自己的 API 服务", "连接本机 Codex", "本地 OCR + Google 翻译"];
        var cards = names.Select(name => Named<RadioButton>(wizard, name)).ToArray();
        var selected = cards.Single(card => card.Checked);
        var mode = Named<ComboBox>(wizard, "wizardProcessingMode");
        int previousMode = mode.SelectedIndex;
        Check(!Descendants(wizard).Any(control => control.Name == "wizardBackend"), "Service cards replace the legacy provider dropdown");
        Check(cards.Select(card => card.Text).SequenceEqual(labels), "The four service cards use the intended order and labels");
        var viewport = Named<Panel>(wizard, "wizardViewport");
        wizard.PerformLayout(); Application.DoEvents();
        var positions = cards.Select(card => card.RectangleToScreen(card.ClientRectangle).Top).ToArray();
        Check(positions.Zip(positions.Skip(1)).All(pair => pair.First < pair.Second), "Service cards are visually ordered from account credits to Google");
        var footer = Named<Button>(wizard, "wizardNext").RectangleToScreen(Named<Button>(wizard, "wizardNext").ClientRectangle);
        foreach (var card in cards)
        {
            viewport.ScrollControlIntoView(card); Application.DoEvents();
            Rectangle bounds = viewport.RectangleToClient(card.RectangleToScreen(card.ClientRectangle));
            Check(card.Visible && card.Enabled && card.CanSelect && card.Focus(), "Every service card is keyboard-focusable");
            Check(viewport.ClientRectangle.Contains(bounds), "Every service card can be scrolled completely into view at the minimum window size");
            Check(card.RectangleToScreen(card.ClientRectangle).Bottom <= footer.Top, "The footer never covers a service card");
        }
        selected.Checked = true;
        selected.Focus();
        mode.SelectedIndex = previousMode;
        viewport.AutoScrollPosition = Point.Empty;
    }

    private static void MainFormModalGuards()
    {
        using var form = new MainForm(true);
        try
        {
            SetField(form, "setupDialogOpen", true);
            var status = Field<Label>(form, "status");
            status.Text = "modal guard sentinel";
            using var pixel = new Bitmap(1, 1);
            var snapshot = new SelectionCaptureSnapshot(VisionImage.Prepare(pixel), default);
            foreach (bool recognizeOnly in new[] { false, true })
            {
                // Even if the modal guard regresses, a disconnected invalid anchor stops
                // this fixture before capture or a service request can be attempted.
                var task = (Task)Invoke(form, "CaptureAndTranslate", recognizeOnly, "setup guard fixture", 0L, snapshot)!;
                Check(task.IsCompletedSuccessfully && status.Text == "modal guard sentinel",
                    "An open setup dialog blocks capture entry before changing request state");
            }
            var settings = Field<AppSettings>(form, "settings");
            settings.SpatialSelectionEnabled = settings.ControllerEnabled = true;
            Invoke(form, "BeginSelection", -100L);
            Check(!Field<bool>(form, "selectionActive") && Field<long>(form, "selectionGestureId") == 0,
                "An open setup dialog cannot start a new controller selection");

            var pages = Field<List<Bitmap>>(form, "pages");
            pages.Add(new Bitmap(1, 1)); pages.Add(new Bitmap(1, 1));
            Invoke(form, "ChangePage", 1);
            Check(Field<int>(form, "pageIndex") == 0, "Modal setup blocks result-page navigation");
            var message = Message.Create(IntPtr.Zero, 0x0312, new IntPtr(Hotkeys.Next), IntPtr.Zero);
            Invoke(form, "WndProc", message);
            Check(Field<int>(form, "pageIndex") == 0, "Registered keyboard messages cannot change pages behind setup");

            SetField(form, "overlayVisible", true);
            SetField(form, "hideAt", long.MaxValue);
            Invoke(form, "Poll");
            Check(Field<bool>(form, "overlayVisible") && Field<long>(form, "hideAt") == long.MaxValue,
                "Polling returns before touching overlay/input state while setup is open");
            Check(!Field<VrService>(form, "vr").IsConnected && Field<long>(form, "captureNumber") == 0,
                "Modal guard tests never connect to VR or capture frames");
        }
        finally { Invoke(form, "DisposeServices"); }
    }

    private static void ResetRequiresAReleasedController()
    {
        using var vr = new VrService();
        var hold = Field<LongPressTrigger>(vr, "_translateHold");
        hold.Update(true, false, 0);
        hold.Update(true, true, 100);
        Check(hold.Update(true, true, 1600) && hold.IsHolding && hold.CancelStage,
            "The fixture begins with a live translated controller hold");
        long previousId = hold.GestureId;
        vr.ResetTranslationInput();
        Check(!hold.IsHolding && !hold.CancelStage && hold.Progress == 0 && !hold.CancelRequested,
            "Entering or leaving setup clears ring and cancellation progress");
        Check(!hold.Update(true, true, 5000) && !hold.IsHolding,
            "A button kept down through setup cannot immediately translate or cancel afterward");
        hold.Update(true, false, 5100);
        hold.Update(true, true, 5200);
        Check(hold.Update(true, true, 6700) && hold.GestureId > previousId,
            "A fresh release and full hold rearms with a new gesture identity");

        var selection = Field<SpatialSelectionGesture>(vr, "_selectionGesture");
        HmdMatrix34_t head = Pose(0, 0), left = Pose(-.3f, .1f), right = Pose(.3f, -.1f);
        var origin = ETrackingUniverseOrigin.TrackingUniverseStanding;
        selection.Update(true, false, true, head, left, right, origin, 1, 1.2f);
        var started = selection.Update(true, true, true, head, left, right, origin, 1, 1.2f);
        Check(started.Started is not null && selection.Current is not null, "The fixture has a live rectangle before setup resets input");
        int aborted = 0;
        vr.SelectionAborted += _ => aborted++;
        vr.ResetTranslationInput();
        Check(!selection.IsSelecting && selection.Current is null && aborted == 1 && !vr.IsConnected,
            "Setup reset aborts existing selection safely without initializing SteamVR");
        Check(selection.Update(true, true, true, head, left, right, origin, 1, 1.2f).Started is null,
            "A held selection button also requires release after setup");
    }

    private static HmdMatrix34_t Pose(float x, float y) => new() { m0 = 1, m5 = 1, m10 = 1, m3 = x, m7 = y };

    private static T Field<T>(object target, string name) =>
        (T)(target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(target.GetType().Name, name)).GetValue(target)!;

    private static void SetField(object target, string name, object value) =>
        (target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(target.GetType().Name, name)).SetValue(target, value);

    private static object? Invoke(object target, string name, params object?[] arguments) =>
        (target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(target.GetType().Name, name)).Invoke(target, arguments);

    internal static Task<CreditSession> FixtureLogin(string endpoint, string username, string password, CancellationToken token)
    {
        _ = CreditServiceClient.NormalizeServerUrl(endpoint);
        return Task.FromResult(new CreditSession("synthetic", username, DateTimeOffset.UtcNow.AddHours(1), null));
    }

    private static SetupWizardForm Create(AppSettings initial, Action<AppSettings> save, bool firstRun = true) => new(initial, save, firstRun, authenticate: FixtureLogin)
    {
        StartPosition = FormStartPosition.Manual,
        Location = new Point(-20000, -20000),
        ShowInTaskbar = false
    };

    private static void Show(SetupWizardForm wizard)
    {
        wizard.Show();
        wizard.PerformLayout();
        Application.DoEvents();
        wizard.PerformLayout();
    }

    private static void AdvanceToFinish(SetupWizardForm wizard)
    {
        Check(wizard.CurrentStep == 0 && wizard.TryAdvance() && wizard.CurrentStep == 1,
            "A valid first step advances exactly once");
        Check(wizard.TryAdvance() && wizard.CurrentStep == 2, "Valid service settings reach confirmation");
    }

    private static void SavePreview(SetupWizardForm wizard, string name)
    {
        wizard.PerformLayout();
        Application.DoEvents();
        wizard.PerformLayout();
        var buttons = new[] { Named<Button>(wizard, "wizardNext"), Named<Button>(wizard, "wizardBack"), Named<Button>(wizard, "wizardCancel") };
        foreach (var button in buttons.Where(button => button.Visible))
        {
            Rectangle bounds = wizard.RectangleToClient(button.RectangleToScreen(button.ClientRectangle));
            Check(button.Width > 0 && button.Height > 0 && wizard.ClientRectangle.Contains(bounds),
                "Wizard navigation remains inside the window at its current size");
        }
        for (int first = 0; first < buttons.Length; first++)
            for (int second = first + 1; second < buttons.Length; second++)
                if (buttons[first].Visible && buttons[second].Visible)
                    Check(!buttons[first].RectangleToScreen(buttons[first].ClientRectangle).IntersectsWith(
                        buttons[second].RectangleToScreen(buttons[second].ClientRectangle)), "Wizard navigation buttons must not overlap");
        string directory = Path.Combine(AppContext.BaseDirectory, "artifacts");
        System.IO.Directory.CreateDirectory(directory);
        using var bitmap = new Bitmap(wizard.Width, wizard.Height);
        wizard.DrawToBitmap(bitmap, new Rectangle(Point.Empty, wizard.Size));
        bitmap.Save(Path.Combine(directory, name), System.Drawing.Imaging.ImageFormat.Png);
    }

    private static T Named<T>(Control root, string name) where T : Control =>
        Descendants(root).OfType<T>().Single(control => control.Name == name);

    private static IEnumerable<Control> Descendants(Control root)
    {
        yield return root;
        foreach (Control child in root.Controls)
            foreach (var descendant in Descendants(child)) yield return descendant;
    }

    private static void AssertSettingsEqual(AppSettings expected, AppSettings actual, string message)
    {
        foreach (var property in typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            Check(Equals(property.GetValue(expected), property.GetValue(actual)), message + ": " + property.Name);
    }

    private static void Reject(Action action, string message)
    {
        try { action(); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException) { return; }
        throw new Exception(message);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private sealed class SettingsFixture : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "SteamVrTranslator-SetupTests", Guid.NewGuid().ToString("N"));
        public string SettingsPath => Path.Combine(Directory, "settings.json");

        public void Dispose()
        {
            // Only the exact fixture document and its empty GUID directory are removed.
            if (File.Exists(SettingsPath)) File.Delete(SettingsPath);
            if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, recursive: false);
        }
    }
}
