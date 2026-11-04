namespace SteamVrTranslator;

internal static class SettingsValidation
{
    internal static void ValidateForSave(AppSettings settings, bool requireServiceConfiguration = false)
    {
        ArgumentNullException.ThrowIfNull(settings);
        SettingsStore.ValidateShape(settings);
        ValidateBackendMode(settings);
        if (settings.ProcessingMode == "Ocr") OcrService.NormalizeLanguages(settings.OcrLanguages);
        if (settings.TranslationBackend == "GoogleTranslate")
            GoogleTranslateClient.NormalizeTargetLanguage(settings.TargetLanguage);
        if (requireServiceConfiguration && settings.TranslationBackend == "OpenAI")
            TranslationClient.ValidateSettings(settings);

        var desired = Hotkeys.Parse(settings.Hotkey);
        if (new[] { "Ctrl+Alt+Right", "Ctrl+Alt+Left", "Ctrl+Alt+H" }.Any(h => Hotkeys.Parse(h) == desired))
            throw new ArgumentException("该快捷键已用于翻页或隐藏叠层，请选择其他按键。");
    }

    internal static void ValidateBackendMode(AppSettings settings)
    {
        if (settings.TranslationBackend == "GoogleTranslate" && settings.ProcessingMode is "Image" or "Handwriting")
            throw new InvalidOperationException("Google Translate 只翻译文字，请选择本地 OCR 或本地日语手写 OCR。AI 手写和直接识图请使用 Codex 或视觉 API。");
    }
}
