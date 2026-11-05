using System.Text.Json;
using System.Text.Json.Nodes;

namespace SteamVrTranslator;

internal static class WebSettingsPatch
{
    internal static JsonObject Public(AppSettings settings)
    {
        var result = JsonSerializer.SerializeToNode(settings, LocalWebSurface.Json)!.AsObject();
        result["hasApiKey"] = !string.IsNullOrEmpty(settings.ApiKey);
        return result;
    }

    internal static AppSettings Apply(AppSettings current, JsonElement patch)
    {
        if (patch.ValueKind != JsonValueKind.Object) throw Invalid();
        var next = current.Copy();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in patch.EnumerateObject())
        {
            if (!seen.Add(item.Name)) throw Invalid();
            switch (item.Name)
            {
                case "translationBackend": next.TranslationBackend = Text(item.Value); break;
                case "creditServerUrl": next.CreditServerUrl = Text(item.Value); break;
                case "processingMode": next.ProcessingMode = Text(item.Value); break;
                case "codexExecutablePath": next.CodexExecutablePath = Text(item.Value); break;
                case "codexModel": next.CodexModel = Text(item.Value); break;
                case "codexReasoningEffort": next.CodexReasoningEffort = Text(item.Value); break;
                case "apiBaseUrl": next.ApiBaseUrl = Text(item.Value); break;
                case "apiModel": next.ApiModel = Text(item.Value); break;
                case "apiKey": next.ApiKey = Text(item.Value); break;
                case "apiImageDetail": next.ApiImageDetail = Text(item.Value); break;
                case "targetLanguage": next.TargetLanguage = Text(item.Value); break;
                case "ocrLanguages": next.OcrLanguages = Text(item.Value); break;
                case "hotkey": next.Hotkey = Text(item.Value); break;
                case "captureMode": next.CaptureMode = Text(item.Value); break;
                case "apiStream": next.ApiStream = Flag(item.Value); break;
                case "controllerEnabled": next.ControllerEnabled = Flag(item.Value); break;
                case "spatialSelectionEnabled": next.SpatialSelectionEnabled = Flag(item.Value); break;
                case "showSource": next.ShowSource = Flag(item.Value); break;
                case "cropX": next.CropX = Number(item.Value); break;
                case "cropY": next.CropY = Number(item.Value); break;
                case "cropWidth": next.CropWidth = Number(item.Value); break;
                case "cropHeight": next.CropHeight = Number(item.Value); break;
                case "selectionDistance": next.SelectionDistance = Number(item.Value); break;
                case "overlayWidth": next.OverlayWidth = Number(item.Value); break;
                case "overlayDistance": next.OverlayDistance = Number(item.Value); break;
                case "overlayVerticalOffset": next.OverlayVerticalOffset = Number(item.Value); break;
                case "displaySeconds": next.DisplaySeconds = Integer(item.Value); break;
                default: throw Invalid();
            }
        }
        if (!seen.Contains("apiKey") && ApiProviderPresets.ChangesAuthority(current.ApiBaseUrl, next.ApiBaseUrl)) next.ApiKey = "";
        return next;
    }

    private static string Text(JsonElement v) => v.ValueKind == JsonValueKind.String && v.GetString() is { Length: <= 8192 } s ? s.Trim() : throw Invalid();
    private static bool Flag(JsonElement v) => v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : throw Invalid();
    private static float Number(JsonElement v) => v.ValueKind == JsonValueKind.Number && v.TryGetSingle(out var n) && float.IsFinite(n) ? n : throw Invalid();
    private static int Integer(JsonElement v) => v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : throw Invalid();
    private static ArgumentException Invalid() => new("设置值无效，请检查后重试。");
}
