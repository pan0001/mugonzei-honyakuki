using System.Buffers;
using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Mugonzei.Server;

// Keep the decoded PNG in the queue. Encode it straight to the model connection in bounded chunks.
internal sealed class TranslationImageContent : HttpContent
{
    private readonly byte[] image;
    private readonly ReadOnlyMemory<byte> prefix;
    private readonly ReadOnlyMemory<byte> suffix;

    public TranslationImageContent(string model, string instructions, string targetLanguage, byte[] image)
    {
        this.image = image;
        Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        var envelope = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(envelope);
        writer.WriteStartObject();
        writer.WriteString("model", model);
        writer.WriteBoolean("stream", false);
        writer.WriteStartArray("messages");
        JsonSerializer.Serialize(writer, new { role = "system", content = instructions });
        writer.WriteStartObject();
        writer.WriteString("role", "user");
        writer.WriteStartArray("content");
        JsonSerializer.Serialize(writer, new { type = "text", text = JsonSerializer.Serialize(new { targetLanguage }) });
        writer.WriteStartObject();
        writer.WriteString("type", "image_url");
        writer.WriteStartObject("image_url");
        writer.WritePropertyName("url");
        writer.WriteStringValueSegment("data:image/png;base64,"u8, isFinalSegment: false);
        writer.Flush();
        int imageOffset = envelope.WrittenCount;
        writer.WriteStringValueSegment(ReadOnlySpan<byte>.Empty, isFinalSegment: true);
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
        prefix = envelope.WrittenMemory[..imageOffset];
        suffix = envelope.WrittenMemory[imageOffset..];
    }

    protected override bool TryComputeLength(out long length)
    {
        length = prefix.Length + Base64.GetMaxEncodedToUtf8Length(image.Length) + suffix.Length;
        return true;
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        => SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken token)
    {
        const int inputChunk = 24 * 1024; // divisible by three: only the final chunk can have padding
        await stream.WriteAsync(prefix, token);
        byte[] encoded = new byte[Base64.GetMaxEncodedToUtf8Length(Math.Min(inputChunk, image.Length))];
        for (int offset = 0; offset < image.Length; offset += inputChunk)
        {
            token.ThrowIfCancellationRequested();
            int length = Math.Min(inputChunk, image.Length - offset);
            Base64.EncodeToUtf8(image.AsSpan(offset, length), encoded, out _, out int written);
            await stream.WriteAsync(encoded.AsMemory(0, written), token);
        }
        await stream.WriteAsync(suffix, token);
    }
}
