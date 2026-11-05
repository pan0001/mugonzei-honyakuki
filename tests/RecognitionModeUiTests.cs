using System.Reflection;
using SteamVrTranslator;

public static class RecognitionModeUiTests
{
    private static readonly (string Mode, int Index, string Button)[] Modes =
    [
        ("Ocr", 0, "仅截屏识别"),
        ("Image", 1, "仅识别原文（AI）"),
        ("Handwriting", 2, "仅识别手写（AI）"),
        ("LocalJapanese", 3, "仅识别日语（本地）")
    ];

    public static void Run()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            MainForm? form = null;
            try
            {
                // Do not Show: the Shown handler registers hotkeys and starts polling.
                // Demo construction never reads or writes the user's saved settings.
                form = new MainForm(true);
                using var pixel = new Bitmap(1, 1);
                var invalidSelection = new SelectionCaptureSnapshot(VisionImage.Prepare(pixel), default);
                Check(!Field<VrService>(form, "vr").IsConnected, "UI fixture must remain disconnected from SteamVR");

                foreach (string backend in new[] { "Codex", "OpenAI" })
                    foreach (var mode in Modes)
                    {
                        LoadSavedMode(form, backend, mode);
                        CheckDisclosure(form, backend, mode.Mode);
                        CheckDraftModes(form, mode, invalidSelection);
                        if (mode.Mode is "Image" or "Handwriting")
                            CheckAiDestinationGuard(form, backend, mode, invalidSelection);
                    }
                Check(Field<long>(form, "captureNumber") == 0,
                    "Recognition UI checks must never capture a frame or start a recognition request");
            }
            catch (Exception error) { failure = error; }
            finally
            {
                if (form is not null)
                {
                    try { Invoke(form, "DisposeServices"); }
                    finally { form.Dispose(); }
                }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Exception("Recognition mode UI checks failed", failure);
        Console.WriteLine("PASS saved recognition actions, image-send disclosure, draft isolation and AI request guards (no capture/VR/network)");
    }

    private static void LoadSavedMode(MainForm form, string backend, (string Mode, int Index, string Button) mode)
    {
        var settings = Field<AppSettings>(form, "settings");
        settings.TranslationBackend = backend;
        settings.ProcessingMode = mode.Mode;
        Invoke(form, "LoadSettings");
        Check(Field<ComboBox>(form, "processingMode").SelectedIndex == mode.Index,
            "Saved recognition mode maps to the correct selection: " + mode.Mode);
        Check(Field<ComboBox>(form, "backend").SelectedIndex == (backend == "Codex" ? 0 : 1),
            "Saved recognition backend maps to its own service: " + backend);
        Check(!form.HasPendingAiSettings, "Loading saved recognition settings clears pending AI changes");
        Check(Field<Button>(form, "recognizeButton").Text == mode.Button,
            "The workspace action identifies the saved recognition route: " + backend + "/" + mode.Mode);
        Check(Field<Button>(form, "recognizeButton").Enabled,
            "The saved recognition action remains available while idle");
    }

    private static void CheckDisclosure(MainForm form, string backend, string mode)
    {
        string note = Field<Label>(form, "dataNote").Text;
        string destination = backend == "Codex" ? "Codex" : "API";
        Check(note.Contains(destination, StringComparison.Ordinal),
            "Recognition disclosure identifies the selected service: " + backend + "/" + mode);
        Check(note.Contains("保存后生效", StringComparison.Ordinal)
            && note.Contains("未保存改动", StringComparison.Ordinal),
            "Disclosure distinguishes saved settings from a draft");
        switch (mode)
        {
            case "Image":
                Check(note.Contains("截图", StringComparison.Ordinal)
                    && note.Contains("不经过本地 OCR", StringComparison.Ordinal)
                    && note.Contains("仅识别原文（AI）", StringComparison.Ordinal)
                    && note.Contains("也会发送截图", StringComparison.Ordinal)
                    && note.Contains("只返回原文", StringComparison.Ordinal),
                    "Direct-image recognition-only must explicitly disclose image upload and source-only output");
                break;
            case "Handwriting":
                Check(note.Contains("截图", StringComparison.Ordinal)
                    && note.Contains("一次请求直接返回译文", StringComparison.Ordinal)
                    && note.Contains("核对原文", StringComparison.Ordinal)
                    && note.Contains("仅识别手写（AI）", StringComparison.Ordinal)
                    && note.Contains("定位", StringComparison.Ordinal)
                    && note.Contains("不进行翻译", StringComparison.Ordinal),
                    "Handwriting disclosure distinguishes direct screenshot translation and explicit source reading");
                break;
            case "Ocr":
                Check(note.Contains("本机识别", StringComparison.Ordinal)
                    && note.Contains("文字", StringComparison.Ordinal)
                    && !note.Contains("也会发送截图", StringComparison.Ordinal),
                    "Local OCR must not claim that recognition-only uploads an image");
                break;
            case "LocalJapanese":
                Check(note.Contains("CPU", StringComparison.Ordinal)
                    && note.Contains("图片不会发送", StringComparison.Ordinal)
                    && note.Contains("不调用翻译服务", StringComparison.Ordinal)
                    && note.Contains("不会自动改用云端", StringComparison.Ordinal),
                    "Local Japanese recognition remains local with no silent cloud fallback");
                break;
        }
    }

    private static void CheckDraftModes(MainForm form, (string Mode, int Index, string Button) saved,
        SelectionCaptureSnapshot invalidSelection)
    {
        var mode = Field<ComboBox>(form, "processingMode");
        foreach (var draft in Modes.Where(candidate => candidate.Mode != saved.Mode))
        {
            mode.SelectedIndex = draft.Index;
            Check(form.HasPendingAiSettings, "Changing a recognition mode creates an unapplied draft");
            Check(Field<Button>(form, "recognizeButton").Text == saved.Button,
                "Draft " + draft.Mode + " must not relabel the saved " + saved.Mode + " workspace action");
            CheckRecognitionBlocked(form, invalidSelection);
            mode.SelectedIndex = saved.Index;
            Check(!form.HasPendingAiSettings,
                "Restoring the saved recognition mode clears the pending settings guard");
        }
    }

    private static void CheckAiDestinationGuard(MainForm form, string savedBackend,
        (string Mode, int Index, string Button) saved, SelectionCaptureSnapshot invalidSelection)
    {
        var backend = Field<ComboBox>(form, "backend");
        int original = backend.SelectedIndex;
        backend.SelectedIndex = original == 0 ? 1 : 0;
        Check(form.HasPendingAiSettings, "Changing the AI destination remains an unapplied draft");
        Check(Field<Button>(form, "recognizeButton").Text == saved.Button,
            "Changing the draft backend cannot relabel the saved AI recognition action");
        CheckRecognitionBlocked(form, invalidSelection);
        backend.SelectedIndex = original;
        Check(!form.HasPendingAiSettings, "Restoring the saved AI destination clears the guard");

        // Image-only recognition now uses AI, so a model/address edit must be blocked
        // even when the recognition-mode selection itself has not changed.
        var destination = Field<TextBox>(form, savedBackend == "Codex" ? "codexModel" : "endpoint");
        string originalText = destination.Text;
        destination.Text = savedBackend == "Codex" ? "ui-test-model-draft" : "http://127.0.0.1:1/v1";
        Check(form.HasPendingAiSettings, "An unsaved model/address edit is detected for AI recognition-only");
        CheckRecognitionBlocked(form, invalidSelection);
        destination.Text = originalText;
        Check(!form.HasPendingAiSettings, "Restoring the saved model/address clears the guard");
        CheckDisclosure(form, savedBackend, saved.Mode);
    }

    private static void CheckRecognitionBlocked(MainForm form, SelectionCaptureSnapshot invalidSelection)
    {
        // A disconnected, invalid anchor is a second early-return boundary. If the
        // pending-settings guard regresses, this test still cannot capture or send data.
        Check(!Field<VrService>(form, "vr").IsConnected, "Guard tests must not connect to SteamVR");
        var status = Field<Label>(form, "status");
        status.Text = "";
        var task = (Task)Invoke(form, "CaptureAndTranslate", true, "UI guard test", 0L, invalidSelection)!;
        Check(task.IsCompleted, "Unapplied AI recognition settings must be rejected before asynchronous work");
        task.GetAwaiter().GetResult();
        Check(status.Text.Contains("未保存改动", StringComparison.Ordinal)
            && status.Text.Contains("保存设置", StringComparison.Ordinal),
            "Recognition-only must explain the unsaved AI settings before attempting capture");
        Check(Field<long>(form, "captureNumber") == 0
            && !Field<bool>(form, "busy")
            && !Field<LatestCaptureRequests>(form, "captureRequests").HasPending,
            "Rejected recognition requests must not start or queue capture work");
    }

    private static T Field<T>(MainForm form, string name) =>
        (T)(typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(MainForm).Name, name)).GetValue(form)!;

    private static object? Invoke(MainForm form, string name, params object?[] arguments) =>
        (typeof(MainForm).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(MainForm).Name, name)).Invoke(form, arguments);

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
