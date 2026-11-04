using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SteamVrTranslator;

public sealed class AppSettings
{
    public string TranslationBackend { get; set; } = "Codex";
    public string ProcessingMode { get; set; } = "Ocr";
    public string CodexExecutablePath { get; set; } = "";
    public string CodexModel { get; set; } = "";
    public string CodexReasoningEffort { get; set; } = "";
    public string ApiBaseUrl { get; set; } = "https://api.openai.com/v1";
    public string ApiModel { get; set; } = "";
    public string ApiImageDetail { get; set; } = "Default";
    public bool ApiStream { get; set; }
    [JsonIgnore] public string ApiKey { get; set; } = "";
    public string TargetLanguage { get; set; } = "简体中文";
    public string OcrLanguages { get; set; } = "eng+jpn";
    public string CaptureMode { get; set; } = "VR 左眼";
    public float CropX { get; set; } = 0;
    public float CropY { get; set; } = 0;
    public float CropWidth { get; set; } = 1;
    public float CropHeight { get; set; } = 1;
    public string Hotkey { get; set; } = "Ctrl+Alt+T";
    public bool ControllerEnabled { get; set; } = true;
    public bool SpatialSelectionEnabled { get; set; }
    public float SelectionDistance { get; set; } = 1.2f;
    public float OverlayWidth { get; set; } = 0.9f;
    public float OverlayDistance { get; set; } = 1.2f;
    public float OverlayVerticalOffset { get; set; } = -0.35f;
    public int DisplaySeconds { get; set; } = 20;
    public bool ShowSource { get; set; } = false;

    // All settings are value types or immutable strings, including the in-memory API key.
    public AppSettings Copy() => (AppSettings)MemberwiseClone();
}

public static class SettingsStore
{
    public static string DirectoryPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamVrTranslator");

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SteamVrTranslator/API-key/v1");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    // Existing installations predate the wizard. A saved file is sufficient evidence
    // of setup; never replace a user's settings just because a new flag is absent.
    public static bool NeedsInitialSetup(string? directoryPath = null) =>
        !File.Exists(Path.Combine(directoryPath ?? DirectoryPath, "settings.json"));

    private sealed class SettingsDocument
    {
        public SettingsDocument() { }
        public int Version { get; set; } = 1;
        public AppSettings Settings { get; set; } = new();
        public string ProtectedApiKey { get; set; } = "";
    }

    // The optional directory is for isolated tests/portable tooling. The app uses LocalAppData.
    public static AppSettings Load(string? directoryPath = null)
    {
        string path = Path.Combine(directoryPath ?? DirectoryPath, "settings.json");
        if (!File.Exists(path)) return new AppSettings();
        try
        {
            var document = JsonSerializer.Deserialize<SettingsDocument>(File.ReadAllText(path), JsonOptions)
                ?? throw new JsonException("Empty settings.");
            if (document.Version != 1 || document.Settings is null)
                throw new JsonException("Unsupported settings.");
            ValidateShape(document.Settings);
            if (!string.IsNullOrEmpty(document.ProtectedApiKey))
            {
                byte[] clear = ProtectedData.Unprotect(Convert.FromBase64String(document.ProtectedApiKey),
                    Entropy, DataProtectionScope.CurrentUser);
                try { document.Settings.ApiKey = Encoding.UTF8.GetString(clear); }
                finally { CryptographicOperations.ZeroMemory(clear); }
            }
            return document.Settings;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or CryptographicException)
        {
            throw new InvalidOperationException("设置文件无法读取，或密钥不属于当前 Windows 用户。请重新填写并保存设置。", ex);
        }
    }

    public static void Save(AppSettings settings, string? directoryPath = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ValidateShape(settings);
        string directory = directoryPath ?? DirectoryPath;
        Directory.CreateDirectory(directory);
        var document = new SettingsDocument { Settings = settings };
        if (!string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            byte[] clear = Encoding.UTF8.GetBytes(settings.ApiKey.Trim());
            try
            {
                document.ProtectedApiKey = Convert.ToBase64String(
                    ProtectedData.Protect(clear, Entropy, DataProtectionScope.CurrentUser));
            }
            finally { CryptographicOperations.ZeroMemory(clear); }
        }
        string destination = Path.Combine(directory, "settings.json");
        string temporary = Path.Combine(directory, $"settings.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(document, JsonOptions), new UTF8Encoding(false));
            if (File.Exists(destination)) File.Replace(temporary, destination, null);
            else File.Move(temporary, destination);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    internal static void ValidateShape(AppSettings settings)
    {
        static bool Text(string? value, int maximum, bool allowEmpty = false) => value is not null
            && value.Length <= maximum && (allowEmpty || !string.IsNullOrWhiteSpace(value));
        static bool Number(float value, float minimum, float maximum) => float.IsFinite(value)
            && value >= minimum && value <= maximum;
        if (settings.TranslationBackend is not ("Codex" or "OpenAI" or "GoogleTranslate")
            || settings.ProcessingMode is not ("Ocr" or "Image" or "Handwriting" or "LocalJapanese")
            || settings.ApiImageDetail is not ("Default" or "High" or "Low" or "Auto")
            || !Text(settings.CodexExecutablePath, 4096, true) || !Text(settings.CodexModel, 256, true)
            || settings.CodexReasoningEffort is not ("" or "low" or "medium" or "high")
            || !Text(settings.ApiBaseUrl, 2048) || !Text(settings.ApiModel, 256, true)
            || !Text(settings.TargetLanguage, 100) || !Text(settings.OcrLanguages, 128)
            || !Text(settings.CaptureMode, 64) || !Text(settings.Hotkey, 100)
            || !Number(settings.CropX, 0, 0.99f) || !Number(settings.CropY, 0, 0.99f)
            || !Number(settings.CropWidth, 0.01f, 1) || !Number(settings.CropHeight, 0.01f, 1)
            || settings.CropX + settings.CropWidth > 1.0001f || settings.CropY + settings.CropHeight > 1.0001f
            || !Number(settings.SelectionDistance, 0.4f, 4)
            || !Number(settings.OverlayWidth, 0.3f, 2) || !Number(settings.OverlayDistance, 0.4f, 4)
            || !Number(settings.OverlayVerticalOffset, -2, 2) || settings.DisplaySeconds is < 0 or > 300)
            throw new InvalidOperationException("设置包含缺失文字、无效数字或超出范围的参数，请重新填写并保存设置。");
    }
}
