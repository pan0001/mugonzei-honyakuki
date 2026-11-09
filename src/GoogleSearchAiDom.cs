using System.Text.Json;

namespace SteamVrTranslator;

internal sealed record GoogleSearchAiPage(string State, string Text);

internal static class GoogleSearchAiDom
{
    private static readonly Lazy<string> Script = new(() =>
    {
        using var stream = typeof(GoogleSearchAiDom).Assembly.GetManifestResourceStream("SteamVrTranslator.GoogleSearchAiDom.js")
            ?? throw new InvalidOperationException("Google AI 模式页面适配脚本缺失。");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    internal static string Inspect() => Invoke("inspect", "", null);
    internal static string Attach(string pngBase64, string requestId) => Invoke("attach", requestId, pngBase64);
    internal static string AttachBuffered(string requestId) => Invoke("attachBuffered", requestId, null);
    internal static string Submit(string prompt, string requestId) => Invoke("submit", requestId, prompt);
    internal static string Read(string requestId) => Invoke("read", requestId, null);

    private static string Invoke(string action, string requestId, string? value)
    {
        return $"({Script.Value})({JsonSerializer.Serialize(action)},{JsonSerializer.Serialize(requestId)},{JsonSerializer.Serialize(value)})";
    }
}
