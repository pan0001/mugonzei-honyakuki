using System.Text.Json;
using Mugonzei.Server;

internal static partial class Program
{
    private static async Task HostedScreenTextTests()
    {
        // Synthetic screen text: game dialogue between a performance overlay and desktop chrome.
        const string telemetry = "FPS N/A N/A | GPU 14 % 38 °C 1582 MHz 0.895 V 61 W | CPU 28 % 65 °C 5350 MHz | 延迟 N/A";
        const string dialogue = "听见啦。\n走吧，走吧，走吧。\n門の前で待っていて。";
        const string translated = "听见啦。\n走吧，走吧，走吧。\n请在门前等我。";
        const string chrome = "输入文字并发送 OSC 翻译\n发送翻译\niwaSync3 V2\n12°C\n多云\n搜索\n英\n拼\n11:28 PM\n11/5/2026";
        const string mixed = "VRChat\n" + telemetry + "\nEsc\nR\nTab\nY\nPTT\nV\nF4\nF5\n" + dialogue + "\n" + chrome;
        const string document = "会议记录：按 F4 查看报告。\nGPU 温度上限为 65 °C。\n请在 11/5/2026 前确认。";
        string Vision(string value) => Envelope(JsonSerializer.Serialize(new { status = "text", translation = value }));
        string Translation(string value) => Envelope(JsonSerializer.Serialize(new { translation = value }));
        var responses = new ReplyHandler([
            Vision(telemetry + "\n" + translated),
            Vision("VRChat\n" + telemetry + "\n" + chrome),
            Vision("[无法辨认]\n[无法辨认]。"),
            Vision(document), Translation("请确认编号。"),
            Translation(telemetry),
            Vision(new string(' ', TranslationResult.MaxOutputCharacters) + "x"),
            Translation(new string(' ', TranslationResult.MaxOutputCharacters) + "x")
        ]);
        using var http = new HttpClient(responses);
        using var processor = new TranslationProcessor(Options(), http);
        using var fixture = new Fixture();
        string user = fixture.User(8);
        var image = TranslationInput.Validate(new("image", null, Pixel, "简体中文"));
        var handwriting = TranslationInput.Validate(new("handwriting", null, Pixel, "简体中文"));
        using var service = Service(fixture.Store, processor);
        await service.StartAsync(default);
        try
        {
            async Task<TranslationTaskDto> Run(Guid id, TranslationInput input)
            {
                service.Submit(user, id, input);
                return await Terminal(service, user, id);
            }
            var id = Guid.NewGuid();
            var game = await Run(id, image);
            Check(game.Status == "succeeded" && game.SourceText == "" && game.Translation == translated && game.Balance == 7,
                "Hosted image translation removes peripheral overlays and charges once without synthesizing a source transcript");
            using var recognition = JsonDocument.Parse(responses.Bodies[0]);
            string instructions = recognition.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
            Check(instructions.Contains("performance counters") && instructions.Contains("relevant menu choices") && instructions.Contains("document"),
                "The actual vision call distinguishes peripheral overlays from game content and targeted desktop documents");
            Equal(game, service.Submit(user, id, image), "Retrying the original image returns the stored clean result");
            Throws(409, () => service.Submit(user, id, handwriting));
            Equal(1, responses.Bodies.Count, "Neither a retry nor conflicting reuse sends the image again");

            var blankId = Guid.NewGuid();
            var blank = await Run(blankId, handwriting);
            Check(blank.Status == "no_text" && blank.SourceText == "" && blank.Translation == "" && blank.Balance == 7,
                "An image with only system and game-tool chrome is released as no_text without a debit");
            Equal(blank, service.Submit(user, blankId, handwriting), "Retrying a filtered no-text result is also idempotent");
            Equal("no_text", (await Run(Guid.NewGuid(), TextInput(telemetry))).Status, "Local OCR telemetry sent as text is not translated or charged");
            Equal(2, responses.Bodies.Count, "One call for an overlay-only image, zero calls for known text-only noise");
            Equal("no_text", (await Run(Guid.NewGuid(), handwriting)).Status, "Uncertainty placeholders alone do not count as readable image text");

            var desktop = await Run(Guid.NewGuid(), handwriting);
            Check(desktop.Status == "succeeded" && desktop.SourceText == "" && desktop.Translation == document && desktop.Balance == 6,
                "A handwritten desktop document already in the target language keeps its keys, specs and dates in the direct translation");
            Equal(4, responses.Bodies.Count, "Each image and handwriting result used exactly one provider call");
            const string supplied = "番号を確認してください。";
            var text = await Run(Guid.NewGuid(), TextInput(supplied));
            Check(text.Status == "succeeded" && text.SourceText == supplied && text.Translation == "请确认编号。" && text.Balance == 5,
                "Explicitly supplied text is still returned along with its translation");
            using var textRequest = JsonDocument.Parse(responses.Bodies[4]);
            using var payload = JsonDocument.Parse(textRequest.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
            Equal(supplied, payload.RootElement.GetProperty("sourceText").GetString(), "Text mode uses the supplied source in one text-only provider request");
            var failed = await Run(Guid.NewGuid(), TextInput("番号を確認してください。"));
            Check(failed.Status == "failed" && failed.Error == "invalid_model_output" && failed.Balance == 5,
                "When real source is present but the provider returns only telemetry, the unusable translation is not charged");
            foreach (var input in new[] { image, TextInput("hello") })
            {
                var overlong = await Run(Guid.NewGuid(), input);
                Check(overlong.Status == "failed" && overlong.Error == "invalid_model_output" && overlong.Balance == 5,
                    "Raw provider length limits are enforced before text cleanup");
            }
            Equal(0L, fixture.Store.GetAccount(user).Reserved, "Filtering releases every reservation");
            Equal(3, fixture.Store.GetLedger(user).Count(row => row.Kind == "translation"), "Each successful job is charged exactly once");
            Equal(8, responses.Bodies.Count, "Neither invalid nor overlong replies are retried");
            // Simulate a completed task persisted by an earlier deployment with unfiltered source.
            var legacyId = Guid.NewGuid();
            string key = legacyId.ToString("D");
            fixture.Store.ReserveTranslation(user, key, image.BodyHash(), image.PersistenceJson());
            fixture.Store.StartTranslation(user, key);
            var legacy = new TranslationResult(false, mixed, translated);
            fixture.Store.CompleteTranslation(user, key, JsonSerializer.Serialize(legacy, TranslationInput.Json), charge: true);
            int calls = responses.Bodies.Count;
            var replay = service.Submit(user, legacyId, image);
            Check(replay.SourceText == mixed && replay.Translation == translated && replay.Balance == 4 && calls == responses.Bodies.Count,
                "Pre-upgrade results remain byte-for-byte intact on replay, with no new upstream request or account debit");
        }
        finally { await service.StopAsync(default); }
        await FailedImageAttemptsAreNotRetried();
    }

    private static async Task FailedImageAttemptsAreNotRetried()
    {
        using var fixture = new Fixture();
        string user = fixture.User(1);
        foreach (bool disconnect in new[] { false, true })
        {
            var handler = new UnavailableModelHandler(disconnect);
            using var http = new HttpClient(handler);
            using var processor = new TranslationProcessor(Options(), http);
            using var service = Service(fixture.Store, processor);
            await service.StartAsync(default);
            try
            {
                Guid id = Guid.NewGuid();
                var input = TranslationInput.Validate(new(disconnect ? "handwriting" : "image", null, Pixel, "日本語"));
                service.Submit(user, id, input);
                var result = await Terminal(service, user, id);
                Check(result.Status == "failed" && result.Balance == 1 && fixture.Store.GetAccount(user).Reserved == 0,
                    "A provider disconnect or service error is terminal and releases the reservation");
                Equal(result, service.Submit(user, id, input), "Client retries recover the failed result");
                Equal(1, handler.Requests, "A failed screenshot request never falls back to another provider call");
            }
            finally { await service.StopAsync(default); }
        }
        Equal(0, fixture.Store.GetLedger(user).Count(row => row.Kind == "translation"), "No failed image attempt creates a translation debit");
    }

    private sealed class UnavailableModelHandler(bool disconnect) : HttpMessageHandler
    {
        internal int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            if (disconnect) throw new HttpRequestException("Synthetic disconnect");
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
        }
    }
}
