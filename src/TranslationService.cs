namespace SteamVrTranslator;

public sealed class TranslationService : IDisposable
{
    private readonly TranslationClient api = new();
    private readonly CodexTranslationClient codex = new();
    private readonly GoogleTranslateWebClient google = new();
    private readonly GoogleSearchAiWebClient searchAi = new();
    private readonly Func<byte[], string, CancellationToken, Task<string>> submitSearchImage;

    public TranslationService() => submitSearchImage = searchAi.TranslateImageAsync;
    internal TranslationService(Func<byte[], string, CancellationToken, Task<string>> submitSearchImage) =>
        this.submitSearchImage = submitSearchImage ?? throw new ArgumentNullException(nameof(submitSearchImage));

    public Task<string> TranslateAsync(string text, AppSettings settings, CancellationToken token) => settings.TranslationBackend switch
    {
        "Codex" => codex.TranslateAsync(text, settings, token),
        "OpenAI" => api.TranslateAsync(text, settings, token),
        "GoogleTranslate" => google.TranslateAsync(text, settings, token),
        "GoogleSearchAI" => throw new InvalidOperationException("Google 搜索 AI 模式只接收截图；纯文字翻译可选择 Google Translate。"),
        _ => throw new InvalidOperationException("未知翻译后端，请重新选择并保存设置。")
    };

    public void Dispose() { api.Dispose(); google.Dispose(); searchAi.Dispose(); }

    public Task ShowGoogleBrowserAsync() => google.ShowBrowserAsync();
    public Task ShowGoogleSearchAiBrowserAsync() => searchAi.ShowBrowserAsync();

    public Task<string> AnalyzeImageAsync(byte[] png, AppSettings settings, CancellationToken token) => settings.TranslationBackend switch
    {
        "Codex" => codex.AnalyzeImageAsync(png, settings, token),
        "OpenAI" => api.AnalyzeImageAsync(png, settings, token),
        "GoogleSearchAI" => TranslateSearchImageAsync(png, settings, token),
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
        "GoogleSearchAI" => throw new InvalidOperationException("Google 搜索 AI 模式只返回译文，不提供单独转写。"),
        _ => throw new InvalidOperationException("未知翻译后端，请重新选择并保存设置。")
    };

    private async Task<string> TranslateSearchImageAsync(byte[] png, AppSettings settings, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        VisionImage.ValidatePng(png);
        if (string.IsNullOrWhiteSpace(settings.TargetLanguage) || settings.TargetLanguage.Length > 100
            || settings.TargetLanguage.Any(char.IsControl))
            throw new InvalidOperationException("请填写有效的目标语言（最多 100 个字符）。");
        string prompt = VisionPrompt.TranslationInstructions(settings) + "\n" + VisionPrompt.BuildRequest(settings);
        string answer = await submitSearchImage(png, prompt, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(answer) || answer.Length > TranslationClient.MaxOutputCharacters)
            throw new InvalidOperationException("Google 搜索 AI 网页未返回有效的完整回答。");
        return VisionPrompt.NormalizeResult(answer);
    }
}
