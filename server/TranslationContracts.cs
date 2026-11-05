using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mugonzei.Server;

public sealed record TranslationJob(string Id, string UserId, string BodyHash, string InputJson,
    string Status, string? ResultJson, string? ErrorCode, long Balance);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TranslationRequest(string Mode, string? Text, string? ImageBase64, string TargetLanguage);

public sealed record TranslationInput(string Mode, string? Text, byte[]? Image, string TargetLanguage)
{
    public const int MaxTextCharacters = 16_000;
    public const int MaxImageBytes = 12 * 1024 * 1024;
    public const int MaxRequestBytes = MaxImageBytes / 3 * 4 + 65_536;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 16 };

    public static TranslationInput Validate(TranslationRequest request)
    {
        if (request.Mode is not ("text" or "image" or "handwriting"))
            throw new ApiException(400, "invalid_mode", "请选择文字、图片或手写识别。");
        string language = (request.TargetLanguage ?? "").Trim();
        if (language.Length is < 1 or > 100 || language.Any(char.IsControl))
            throw new ApiException(400, "invalid_language", "目标语言无效。");
        if (request.Mode == "text")
        {
            if (request.Text is null || request.Text.Length > MaxTextCharacters || request.ImageBase64 is not null)
                throw new ApiException(400, "invalid_text", "文字请求需要不超过 16000 个字符的原文，且不能携带图片。");
            return new(request.Mode, request.Text, null, language);
        }
        if (request.Text is not null || string.IsNullOrEmpty(request.ImageBase64)
            || request.ImageBase64.Length > MaxImageBytes / 3 * 4)
            throw new ApiException(400, "invalid_image", "图片请求需要不超过 12 MiB 的 PNG 图片，且不能携带原文。");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(request.ImageBase64); }
        catch (FormatException) { throw new ApiException(400, "invalid_image", "图片编码无效。"); }
        ValidatePng(bytes);
        return new(request.Mode, null, bytes, language);
    }

    // This metadata is safe to persist: the image itself exists only in the work queue.
    public string PersistenceJson() => JsonSerializer.Serialize(new
    {
        mode = Mode, text = Text, targetLanguage = TargetLanguage,
        imageSha256 = Image is null ? null : Convert.ToHexString(SHA256.HashData(Image))
    }, Json);

    public string BodyHash() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(PersistenceJson())));

    private static void ValidatePng(byte[] bytes)
    {
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (bytes.Length is < 45 or > MaxImageBytes || !bytes.AsSpan(0, 8).SequenceEqual(signature)
            || BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(8, 4)) != 13
            || !bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8)) throw BadImage();
        uint width = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4));
        uint height = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4));
        if (width is 0 or > 4096 || height is 0 or > 4096 || (ulong)width * height > 12_000_000) throw BadImage();
        bool data = false;
        for (int offset = 8; offset <= bytes.Length - 12;)
        {
            uint length = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));
            if (length > (uint)(bytes.Length - offset - 12)) throw BadImage();
            var type = bytes.AsSpan(offset + 4, 4);
            if (offset != 8 && type.SequenceEqual("IHDR"u8)) throw BadImage();
            if (type.SequenceEqual("IDAT"u8) && length > 0) data = true;
            if (type.SequenceEqual("IEND"u8))
            {
                if (length == 0 && data && offset + 12 == bytes.Length) return;
                throw BadImage();
            }
            offset += checked((int)length + 12);
        }
        throw BadImage();
    }

    private static ApiException BadImage() => new(400, "invalid_image", "PNG 图片不完整，或尺寸超过 4096 / 1200 万像素限制。");
}

public sealed record TranslationResult(bool NoText, string SourceText, string Translation)
{
    public const int MaxOutputCharacters = 32_000;
    public void Validate(string mode)
    {
        bool imageWithoutTranscript = mode is "image" or "handwriting" && SourceText == "";
        if (SourceText is null || Translation is null || SourceText.Length > TranslationInput.MaxTextCharacters
            || Translation.Length > MaxOutputCharacters
            || (NoText ? SourceText.Length != 0 || Translation.Length != 0
                : (!imageWithoutTranscript && string.IsNullOrWhiteSpace(SourceText)) || string.IsNullOrWhiteSpace(Translation)))
            throw new TranslationProcessingException("invalid_model_output");
    }
}

public sealed record TranslationTaskDto(string Id, string Status, string SourceText, string Translation, string? Error, long Balance)
{
    public static TranslationTaskDto From(TranslationJob job)
    {
        TranslationResult? result = job.ResultJson is null ? null
            : JsonSerializer.Deserialize<TranslationResult>(job.ResultJson, TranslationInput.Json);
        return new(Guid.Parse(job.Id).ToString("D"), job.Status, result?.SourceText ?? "", result?.Translation ?? "", job.ErrorCode, job.Balance);
    }
}

public interface ITranslationProcessor
{
    bool IsAvailable { get; }
    Task<TranslationResult> ProcessAsync(TranslationInput input, CancellationToken cancellationToken);
}

public sealed class TranslationProcessingException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

public sealed class TranslationOptions
{
    public string BaseUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 120;
    public int Concurrency { get; set; } = 2;
    public int QueueCapacity { get; set; } = 16;
    public bool Mock { get; set; }
}

internal static class TranslationStreams
{
    public static async Task<byte[]> ReadBoundedAsync(Stream stream, int limit, CancellationToken token)
    {
        using var output = new MemoryStream();
        byte[] buffer = new byte[16_384];
        while (true)
        {
            int read = await stream.ReadAsync(buffer, token);
            if (read == 0) return output.ToArray();
            if (output.Length + read > limit) throw new TranslationProcessingException("response_too_large");
            await output.WriteAsync(buffer.AsMemory(0, read), token);
        }
    }
}
