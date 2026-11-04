namespace SteamVrTranslator;

internal sealed record ApiProviderPreset(string Name, string BaseUrl, string Note);

internal static class ApiProviderPresets
{
    public static IReadOnlyList<ApiProviderPreset> All { get; } = Array.AsReadOnly(new[]
    {
        new ApiProviderPreset("自定义（保留当前地址）", "",
            "使用支持 OpenAI 兼容图片请求的接口，手动填写可用的视觉模型名称。"),
        new ApiProviderPreset("硅基流动（中国站）", "https://api.siliconflow.cn/v1",
            "使用中国站密钥，手动填写该账号可用的视觉模型名称。"),
        new ApiProviderPreset("SiliconFlow（国际站）", "https://api.siliconflow.com/v1",
            "使用国际站密钥，手动填写该账号可用的视觉模型名称。"),
        // The official compatibility guide confirms this existing domain remains supported.
        // https://help.aliyun.com/zh/model-studio/compatibility-of-openai-with-dashscope
        new ApiProviderPreset("阿里云百炼（北京）", "https://dashscope.aliyuncs.com/compatible-mode/v1",
            "使用北京地域密钥；模型要求流式时勾选下方选项。也可填写控制台的业务空间专属地址。"),
        new ApiProviderPreset("Gemini（OpenAI 兼容接口）", "https://generativelanguage.googleapis.com/v1beta/openai/",
            "使用 Gemini API 密钥，手动填写该账号可用的视觉模型名称。"),
        new ApiProviderPreset("OpenRouter", "https://openrouter.ai/api/v1",
            "使用 OpenRouter 密钥，手动填写支持图片输入的模型标识。"),
        new ApiProviderPreset("Ollama（本机）", "http://localhost:11434/v1/",
            "先启动本机 Ollama 并安装、加载视觉模型；默认无需密钥，若服务要求鉴权则填写对应密钥。"),
        // https://lmstudio.ai/docs/developer/openai-compat
        // https://lmstudio.ai/docs/developer/core/authentication
        new ApiProviderPreset("LM Studio（本机）", "http://localhost:1234/v1/",
            "先启动本机服务器并加载视觉模型；默认无需密钥，启用 API Token 鉴权时需填写密钥。")
    });

    /// <summary>Whether an address change may send credentials to a different origin.</summary>
    public static bool ChangesAuthority(string oldAddress, string newAddress)
    {
        if (string.Equals(oldAddress, newAddress, StringComparison.Ordinal)) return false;
        if (!Uri.TryCreate(oldAddress?.Trim(), UriKind.Absolute, out var oldUri)
            || !Uri.TryCreate(newAddress?.Trim(), UriKind.Absolute, out var newUri)
            || !IsWebAddress(oldUri) || !IsWebAddress(newUri))
            return true;

        try
        {
            // Uri normalizes scheme, host and default ports; IdnHost also equates IDN spelling.
            return !string.Equals(oldUri.Scheme, newUri.Scheme, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(oldUri.IdnHost, newUri.IdnHost, StringComparison.OrdinalIgnoreCase)
                || oldUri.Port != newUri.Port;
        }
        catch (UriFormatException)
        {
            return true;
        }
    }

    private static bool IsWebAddress(Uri uri) =>
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && !string.IsNullOrEmpty(uri.Host) && string.IsNullOrEmpty(uri.UserInfo);
}
