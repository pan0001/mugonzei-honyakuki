namespace SteamVrTranslator;

public sealed class TranslationService : IDisposable
{
    private readonly TranslationClient api = new();
    private readonly CodexTranslationClient codex = new();
    private readonly GoogleTranslateWebClient google = new();

    public Task<string> TranslateAsync(string text, AppSettings settings, CancellationToken token) => settings.TranslationBackend switch
    {
        "Codex" => codex.TranslateAsync(text, settings, token),
        "OpenAI" => api.TranslateAsync(text, settings, token),
        "GoogleTranslate" => google.TranslateAsync(text, settings, token),
        _ => throw new InvalidOperationException("未知翻译后端，请重新选择并保存设置。")
    };

    public void Dispose() { api.Dispose(); google.Dispose(); }

    public Task ShowGoogleBrowserAsync() => google.ShowBrowserAsync();

    public Task<string> AnalyzeImageAsync(byte[] png, AppSettings settings, CancellationToken token) => settings.TranslationBackend switch
    {
        "Codex" => codex.AnalyzeImageAsync(png, settings, token),
        "OpenAI" => api.AnalyzeImageAsync(png, settings, token),
        "GoogleTranslate" => throw new InvalidOperationException("Google Translate 仅翻译文字，请选择本地 OCR 或本地日语手写 OCR。"),
        _ => throw new InvalidOperationException("未知翻译后端，请重新选择并保存设置。")
    };

    public Task<string> RecognizeHandwritingAsync(byte[] png, AppSettings settings, CancellationToken token) => settings.TranslationBackend switch
    {
        "Codex" => HandwritingRecognition.RecognizeAsync(png, settings,
            codex.LocateHandwritingAsync, codex.RecognizeHandwritingViewsAsync, token),
        "OpenAI" => HandwritingRecognition.RecognizeAsync(png, settings,
            api.LocateHandwritingAsync, api.RecognizeHandwritingViewsAsync, token),
        "GoogleTranslate" => throw new InvalidOperationException("Google Translate 不能识别手写图片，请选择本地日语手写 OCR。"),
        _ => throw new InvalidOperationException("未知翻译后端，请重新选择并保存设置。")
    };
}
