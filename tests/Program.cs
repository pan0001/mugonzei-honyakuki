using SteamVrTranslator;

if (args.Length == 2 && args[0] == "--fake-codex")
{
    await CodexTests.RunFakeAsync(args[1]);
    return;
}
if (args.Length == 2 && args[0] == "--fake-local-ocr")
{
    await LocalJapaneseOcrTests.RunFakeAsync(args[1]);
    return;
}

try
{
    if (args.Contains("--setup-regression"))
    {
        SetupWizardTests.Run();
        return;
    }
    if (args.Contains("--codex-settings-regression"))
    {
        await CodexReasoningSettingsTests.RunAsync();
        await CodexVisionTests.RunAsync();
        await TranslationTests.RunAsync();
        RecognitionModeUiTests.Run();
        ApiSettingsUiTests.Run();
        UiLayoutTests.Run();
        return;
    }
    if (args.Contains("--hold-regression"))
    {
        LongPressTests.Run();
        GestureCancelTests.Run();
        await BackgroundCancellationTests.RunAsync();
        VrFrameGuardTests.Run();
        HoldProgressAtlasTests.Run();
        VrTests.Run();
        Console.WriteLine("PASS focused hold animation, native presentation state, cancellation and input continuity regression");
        return;
    }
    SetupWizardTests.Run();
    LongPressTests.Run();
    CaptureRequestTests.Run();
    GestureCancelTests.Run();
    await BackgroundCancellationTests.RunAsync();
    VrFrameGuardTests.Run();
    Console.WriteLine("PASS two-circle hold, scoped cancellation, release/rearm and input loss tests");
    await CodexTests.RunAsync();
    await CodexReasoningSettingsTests.RunAsync();
    Console.WriteLine("PASS Codex protocol, process cancellation and limits tests");
    await TranslationTests.RunAsync();
    await GoogleTranslateTests.RunAsync();
    await GoogleTranslateWebTests.RunAsync();
    GoogleTranslateUiTests.Run();
    RecognitionModeUiTests.Run();
    Console.WriteLine("PASS translation HTTP + settings tests");
    VisionImageTests.Run();
    HandwritingDetailImagesTests.Run();
    OcrAccuracyTests.Run();
    await HandwritingRecognitionTests.RunAsync();
    await ApiVisionTests.RunAsync();
    await HandwritingLocalApiTests.RunAsync();
    await LocalJapaneseOcrTests.RunAsync();
    ApiProviderPresetsTests.Run();
    ApiVisionProbeTests.Run();
    ApiSettingsUiTests.Run();
    await CodexVisionTests.RunAsync();
    Console.WriteLine("PASS direct image preparation, API payload and Codex attachment/cleanup tests");
    OcrRenderTests.Run();
    HoldProgressAtlasTests.Run();
    Console.WriteLine("PASS actual OCR + rendering/pagination tests");
    VrTests.Run();
    SpatialSelectionGeometryTests.Run();
    SelectionOverlayRendererTests.Run();
    SpatialCaptureUiTests.Run();
    Console.WriteLine("PASS spatial selection gesture, perspective crop, anchored snapshots and rendering tests");
    Console.WriteLine("PASS VR pixel readback + input binding tests");
    DesktopTests.Run();
    Console.WriteLine("PASS crop bounds + hotkey parsing tests");
    UiLayoutTests.Run();
    Console.WriteLine("PASS desktop/settings layout renders");
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    Environment.ExitCode = 1;
}
