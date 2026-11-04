namespace SteamVrTranslator;

public static class ApiProviderPresetsTests
{
    public static void Run()
    {
        CatalogAndEndpoints();
        SameAuthorityKeepsCredentials();
        DifferentOrUnknownAuthorityClearsCredentials();
        Console.WriteLine("PASS: API provider presets, compatible endpoints and conservative credential destination comparison");
    }

    private static void CatalogAndEndpoints()
    {
        var presets = ApiProviderPresets.All;
        Check(presets.Count == 8, "Custom entry and seven verified service addresses are present");
        Check(presets[0].Name == "自定义（保留当前地址）" && presets[0].BaseUrl == "",
            "Custom selection preserves the existing address");
        Check(presets.Select(preset => preset.Name).Distinct(StringComparer.Ordinal).Count() == presets.Count,
            "Preset labels are distinct");
        Check(presets.All(preset => !string.IsNullOrWhiteSpace(preset.Name) && !string.IsNullOrWhiteSpace(preset.Note)),
            "Each preset has a label and usage guidance");

        string[] expectedAddresses =
        [
            "https://api.siliconflow.cn/v1",
            "https://api.siliconflow.com/v1",
            "https://dashscope.aliyuncs.com/compatible-mode/v1",
            "https://generativelanguage.googleapis.com/v1beta/openai/",
            "https://openrouter.ai/api/v1",
            "http://localhost:11434/v1/",
            "http://localhost:1234/v1/"
        ];
        Check(presets.Skip(1).Select(preset => preset.BaseUrl).SequenceEqual(expectedAddresses),
            "Preset addresses preserve each provider's region and compatible API prefix");

        foreach (var preset in presets.Skip(1))
        {
            var endpoint = TranslationClient.GetEndpoint(preset.BaseUrl);
            Check(endpoint.AbsoluteUri == preset.BaseUrl.TrimEnd('/') + "/chat/completions",
                "The chat completion path is appended exactly once");
            Check(TranslationClient.GetEndpoint(endpoint.AbsoluteUri).AbsoluteUri == endpoint.AbsoluteUri,
                "A full chat completion endpoint is not appended again");
            Check(endpoint.Scheme == Uri.UriSchemeHttps || endpoint.IsLoopback,
                "Remote presets use HTTPS and the local preset remains on loopback");
        }
    }

    private static void SameAuthorityKeepsCredentials()
    {
        Same("https://example.com/v1", "https://example.com/v2/chat/completions");
        Same("HTTPS://EXAMPLE.COM:443/v1/", "https://example.com/compatible-mode/v1");
        Same("  https://example.com/v1  ", "https://example.com/v2");
        Same("http://localhost:80/v1", "http://LOCALHOST/v2");
        Same("http://localhost:11434/v1", "http://localhost:11434/v1/chat/completions");
        Same("http://localhost:1234/v1/", "http://LOCALHOST:1234/v1/chat/completions");
        Same("https://bücher.example/v1", "https://xn--bcher-kva.example/v2");
        Same("https://[::1]:443/v1", "https://[0:0:0:0:0:0:0:1]/v2");
        Same("", "");
        Same("invalid address", "invalid address");
    }

    private static void DifferentOrUnknownAuthorityClearsCredentials()
    {
        Different("https://api.siliconflow.cn/v1", "https://api.siliconflow.com/v1");
        Different("https://example.com/v1", "https://other.example/v1");
        Different("https://example.com/v1", "http://example.com/v1");
        Different("https://example.com:443/v1", "https://example.com:8443/v1");
        Different("http://localhost:11434/v1", "http://localhost:11435/v1");
        Different("http://localhost:11434/v1", "http://localhost:1234/v1");
        Different("http://localhost:11434/v1", "http://127.0.0.1:11434/v1");
        Different("", "https://example.com/v1");
        Different("invalid address", "https://example.com/v1");
        Different("https://example.com/v1", "/relative/path");
        Different("invalid address", "another invalid address");
        Different("ftp://example.com/first", "ftp://example.com/second");
        Different("file:///C:/first", "file:///C:/second");
        Different("https://account@example.com/v1", "https://example.com/v1");
    }

    private static void Same(string first, string second) =>
        Check(!ApiProviderPresets.ChangesAuthority(first, second)
            && !ApiProviderPresets.ChangesAuthority(second, first), "Equivalent destinations keep credentials");

    private static void Different(string first, string second) =>
        Check(ApiProviderPresets.ChangesAuthority(first, second)
            && ApiProviderPresets.ChangesAuthority(second, first), "Changed or unknown destinations clear credentials");

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("API provider preset test failed: " + message);
    }
}
