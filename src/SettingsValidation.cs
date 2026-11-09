namespace SteamVrTranslator;

internal static class SettingsValidation
{
    internal const string GoogleSearchAiNotice = "翻译时，截图和目标语言会发送给 Google。无需 API 密钥，不消耗本应用积分。Google 可能限流，可用性因账号和地区而异。如需登录或验证，可在服务设置中打开网页处理。";

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
        if (settings.TranslationBackend == "Credits")
            _ = CreditServiceClient.NormalizeServerUrl(settings.CreditServerUrl);

        var desired = Hotkeys.Parse(settings.Hotkey);
        if (new[] { "Ctrl+Alt+Right", "Ctrl+Alt+Left", "Ctrl+Alt+H" }.Any(h => Hotkeys.Parse(h) == desired))
            throw new ArgumentException("该快捷键已用于翻页或隐藏叠层，请选择其他按键。");
    }

    internal static void ValidateBackendMode(AppSettings settings)
    {
        if (settings.TranslationBackend == "GoogleTranslate" && settings.ProcessingMode is "Image" or "Handwriting")
            throw new InvalidOperationException("Google Translate 只翻译文字，请选择本地 OCR 或本地日语手写 OCR。AI 手写和直接识图请使用 Codex 或视觉 API。");
        if (settings.TranslationBackend == "GoogleSearchAI" && settings.ProcessingMode != "Image")
            throw new InvalidOperationException("Google 搜索 AI 模式需要直接发送截图，请选择“直接发送截图给模型”。");
        if (settings.TranslationBackend == "GoogleSearchAI" && settings.TargetLanguage.Any(char.IsControl))
            throw new InvalidOperationException("请填写有效的目标语言。");
    }
}
