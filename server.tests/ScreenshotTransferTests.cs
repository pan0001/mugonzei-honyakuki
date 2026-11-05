using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.Json;
using Mugonzei.Server;

internal static partial class Program
{
    private static async Task ScreenshotTransferTests()
    {
        const string language = "日本語 & \"English\"";
        byte[] png = SyntheticCommentPng();
        string encoded = Convert.ToBase64String(png);
        var previous = TranslationInput.Validate(new("handwriting", null, encoded, language));
        // Property casing, escaped base64 characters and whitespace were accepted by the old public API.
        string spaced = encoded.Insert(52, " \t\r\n");
        byte[] wire = JsonSerializer.SerializeToUtf8Bytes(new { IMAGEBASE64 = spaced, mode = "handwriting", targetLanguage = " " + language + " " });
        foreach (long? hint in new long?[] { wire.Length, null, 0, 9, wire.Length + 15 })
        {
            var result = await TranslationInput.ReadRequestAsync(new MemoryStream(wire), hint, default);
            Check(result.Image!.AsSpan().SequenceEqual(png), "Length hints and JSON escapes never alter the incoming PNG");
            Equal(previous.PersistenceJson(), result.PersistenceJson(), "Queue metadata preserves the old digest and does not contain image data");
            Equal(previous.BodyHash(), result.BodyHash(), "Image replay hashes use the same canonical metadata");
        }
        using var upstream = new HttpClient(new ImageWireHandler(png, language));
        using var processor = new TranslationProcessor(Options(), upstream);
        var translated = await processor.ProcessAsync(previous, default);
        Check(translated.NoText, "A streamed image with no main content still returns no_text");

        async Task Parse(string json)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            await TranslationInput.ReadRequestAsync(new MemoryStream(bytes), bytes.Length, default);
        }
        await ThrowsApiAsync(400, () => Parse("""{"mode":"image","imageBase64":"!","targetLanguage":"zh"}"""));
        await ThrowsApiAsync(400, () => Parse("""{"mode":"image","imageBase64":"AQID","targetLanguage":"zh"}"""));
        await ThrowsApiAsync(400, () => Parse("""{"mode":"text","text":"hello","imageBase64":"","targetLanguage":"zh"}"""));
        try { await Parse("""{"mode":"text","text":"hello","model":"override","targetLanguage":"zh"}"""); throw new Exception("Unexpected wire field accepted"); }
        catch (JsonException) { checks++; }

        // Count decoded base64 characters (including spaces) even when the wire contains a longer JSON escape.
        const int encodedLimit = TranslationInput.MaxImageBytes / 3 * 4;
        byte[] prefix = """{"mode":"image","targetLanguage":"zh","imageBase64":"""u8.ToArray();
        byte[] start = Encoding.ASCII.GetBytes("\"" + Pixel.Replace("/", "\\u002F", StringComparison.Ordinal));
        byte[] padded = new byte[prefix.Length + start.Length + (encodedLimit - Pixel.Length) + 3];
        prefix.CopyTo(padded, 0); start.CopyTo(padded, prefix.Length);
        padded.AsSpan(prefix.Length + start.Length).Fill((byte)' ');
        padded[^3] = (byte)'"'; padded[^2] = (byte)'}';
        var exact = await TranslationInput.ReadRequestAsync(new MemoryStream(padded), padded.Length, default);
        Check(exact.Image!.AsSpan().SequenceEqual(Convert.FromBase64String(Pixel)), "The exact encoded image limit ignores JSON escape overhead");
        padded[^3] = (byte)' '; padded[^2] = (byte)'"'; padded[^1] = (byte)'}';
        await ThrowsApiAsync(400, () => TranslationInput.ReadRequestAsync(new MemoryStream(padded), padded.Length, default));

        // Unknown or forged length cannot bypass the total wire limit.
        byte[] excessive = new byte[TranslationInput.MaxRequestBytes + 1];
        foreach (long? hint in new long?[] { null, excessive.Length, TranslationInput.MaxRequestBytes })
        {
            try { await TranslationInput.ReadRequestAsync(new MemoryStream(excessive), hint, default); throw new Exception("Oversize request accepted"); }
            catch (TranslationProcessingException error) when (error.Code == "response_too_large") { checks++; }
        }
    }

    private static byte[] SyntheticCommentPng()
    {
        // Add a valid PNG tEXt chunk before IEND. Its public text crosses multiple 24 KiB encoding boundaries.
        byte[] pixel = Convert.FromBase64String(Pixel);
        const int textLength = 65536;
        byte[] png = new byte[pixel.Length + textLength + 12];
        int offset = pixel.Length - 12;
        pixel.AsSpan(0, offset).CopyTo(png);
        Span<byte> chunk = png.AsSpan(offset);
        BinaryPrimitives.WriteInt32BigEndian(chunk, textLength);
        "tEXtComment\0"u8.CopyTo(chunk[4..]);
        chunk.Slice(16, textLength - 8).Fill((byte)'x');
        BinaryPrimitives.WriteUInt32BigEndian(chunk[(8 + textLength)..], 0x9BC0D218); // CRC32(tEXt + Comment\0 + x...)
        pixel.AsSpan(offset).CopyTo(png.AsSpan(png.Length - 12));
        return png;
    }

    private sealed class ImageWireHandler(byte[] expected, string language) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            using var wire = new MemoryStream();
            await request.Content!.CopyToAsync(wire, token);
            Equal((long?)wire.Length, request.Content.Headers.ContentLength, "The streamed request advertises its exact byte count");
            wire.Position = 0;
            using var doc = await JsonDocument.ParseAsync(wire, cancellationToken: token);
            var parts = doc.RootElement.GetProperty("messages")[1].GetProperty("content");
            using var data = JsonDocument.Parse(parts[0].GetProperty("text").GetString()!);
            Equal(language, data.RootElement.GetProperty("targetLanguage").GetString(), "Dynamic target language remains JSON-escaped");
            string url = parts[1].GetProperty("image_url").GetProperty("url").GetString()!;
            Check(url == "data:image/png;base64," + Convert.ToBase64String(expected), "All encoded chunks form one correctly padded PNG URL");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Envelope("""{"status":"no_text","translation":""}""")) };
        }
    }
}
