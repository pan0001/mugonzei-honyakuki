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
        return Validate(request.Mode, request.Text, request.TargetLanguage, request.ImageBase64 is not null, () =>
        {
            if (string.IsNullOrEmpty(request.ImageBase64) || request.ImageBase64.Length > MaxImageBytes / 3 * 4)
                return null;
            try { return Convert.FromBase64String(request.ImageBase64); }
            catch (FormatException) { return null; }
        });
    }

    public static async Task<TranslationInput> ReadRequestAsync(Stream stream, long? contentLength, CancellationToken token)
    {
        var body = await TranslationStreams.ReadBoundedAsync(stream, MaxRequestBytes, token, contentLength);
        var request = JsonSerializer.Deserialize<Upload>(body.Span, Json) ?? throw new JsonException();
        return Validate(request.Mode, request.Text, request.TargetLanguage, request.ImageBase64 is not null, () => request.ImageBase64?.Bytes);
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record Upload(string Mode, string? Text, UploadImage? ImageBase64, string TargetLanguage);

    [JsonConverter(typeof(UploadImageConverter))]
    private sealed record UploadImage(byte[]? Bytes);

    private sealed class UploadImageConverter : JsonConverter<UploadImage>
    {
        public override UploadImage Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String) throw new JsonException();
            // The reader is over our single bounded UTF-8 buffer. Keep the old length limit
            // on the JSON-decoded base64 (including whitespace), not on JSON escape sequences.
            const int limit = MaxImageBytes / 3 * 4;
            var raw = reader.ValueSpan;
            int length = raw.Length;
            while (length > limit && reader.ValueIsEscaped)
            {
                int index = raw.IndexOf((byte)'\\');
                if (index < 0) break;
                int extra = raw[index + 1] == (byte)'u' ? 5 : 1;
                length -= extra;
                raw = raw[(index + extra + 1)..];
            }
            return new(length <= limit && reader.TryGetBytesFromBase64(out var bytes) ? bytes : null);
        }

        public override void Write(Utf8JsonWriter writer, UploadImage value, JsonSerializerOptions options)
            => writer.WriteBase64StringValue(value.Bytes);
    }

    private static TranslationInput Validate(string mode, string? text, string? targetLanguage, bool hasImage, Func<byte[]?> decode)
    {
        if (mode is not ("text" or "image" or "handwriting"))
            throw new ApiException(400, "invalid_mode", "请选择文字、图片或手写识别。");
        string language = (targetLanguage ?? "").Trim();
        if (language.Length is < 1 or > 100 || language.Any(char.IsControl))
            throw new ApiException(400, "invalid_language", "目标语言无效。");
        if (mode == "text")
        {
            if (text is null || text.Length > MaxTextCharacters || hasImage)
                throw new ApiException(400, "invalid_text", "文字请求需要不超过 16000 个字符的原文，且不能携带图片。");
            return new(mode, text, null, language);
        }
        if (text is not null || !hasImage || decode() is not byte[] bytes)
            throw new ApiException(400, "invalid_image", "图片请求需要不超过 12 MiB 的 PNG 图片，且不能携带原文。");
        ValidatePng(bytes);
        return new(mode, null, bytes, language);
    }

    // This metadata is safe to persist: the image itself exists only in the work queue.
    public string PersistenceJson() => JsonSerializer.Serialize(new
    {
        mode = Mode, text = Text, targetLanguage = TargetLanguage,
        imageSha256 = Image is null ? null : Convert.ToHexString(SHA256.HashData(Image))
    }, Json);

    public string BodyHash() => HashMetadata(PersistenceJson());
    internal static string HashMetadata(string json) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));

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
    public static async Task<ReadOnlyMemory<byte>> ReadBoundedAsync(Stream stream, int limit, CancellationToken token, long? contentLength)
    {
        if (contentLength > limit) throw new TranslationProcessingException("response_too_large");
        int capacity = contentLength is >= 0 ? (int)contentLength : Math.Min(limit, 16_384);
        byte[] buffer = GC.AllocateUninitializedArray<byte>(capacity);
        int used = 0;
        byte[] probe = new byte[1];
        while (true)
        {
            if (used == buffer.Length)
            {
                // Probe the boundary before growing. Honest Content-Length needs only one buffer;
                // chunked or misleading lengths still cannot bypass the hard limit.
                if (await stream.ReadAsync(probe, token) == 0) return buffer.AsMemory(0, used);
                if (used == limit) throw new TranslationProcessingException("response_too_large");
                Array.Resize(ref buffer, Math.Min(limit, Math.Max(16_384, used * 2)));
                buffer[used++] = probe[0];
                continue;
            }
            int read = await stream.ReadAsync(buffer.AsMemory(used), token);
            if (read == 0) return buffer.AsMemory(0, used);
            used += read;
        }
    }
}
