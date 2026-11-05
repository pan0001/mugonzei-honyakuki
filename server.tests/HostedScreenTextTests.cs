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
        string Vision(string value) => Envelope(JsonSerializer.Serialize(new { status = "text", sourceText = value }));
        string Translation(string value) => Envelope(JsonSerializer.Serialize(new { translation = value }));
        var responses = new ReplyHandler([
            Vision(mixed), Translation(telemetry + "\n" + translated),
            Vision("VRChat\n" + telemetry + "\n" + chrome),
            Vision("[无法辨认]\n[无法辨认]。"),
            Vision(document), Translation(document),
            Translation(telemetry),
            Vision(new string(' ', TranslationInput.MaxTextCharacters) + "x"),
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
            Check(game.Status == "succeeded" && game.SourceText == dialogue && game.Translation == translated && game.Balance == 7,
                "Hosted recognition retains substantive multilingual dialogue, strips peripheral overlays at both stages and charges once");
            using var recognition = JsonDocument.Parse(responses.Bodies[0]);
            string instructions = recognition.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
            Check(instructions.Contains("performance counters") && instructions.Contains("relevant menu choices") && instructions.Contains("document"),
                "The actual vision call distinguishes peripheral overlays from game content and targeted desktop documents");
            using var request = JsonDocument.Parse(responses.Bodies[1]);
            var payload = request.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
            using var translationInput = JsonDocument.Parse(payload);
            Equal(dialogue, translationInput.RootElement.GetProperty("sourceText").GetString(), "Only cleaned source reaches the hosted translation stage");
            Equal(game, service.Submit(user, id, image), "Retrying the original image returns the stored clean result");
            Throws(409, () => service.Submit(user, id, handwriting));
            Equal(2, responses.Bodies.Count, "Neither a retry nor conflicting reuse triggers a third model call");

            var blankId = Guid.NewGuid();
            var blank = await Run(blankId, handwriting);
            Check(blank.Status == "no_text" && blank.SourceText == "" && blank.Translation == "" && blank.Balance == 7,
                "An image with only system and game-tool chrome is released as no_text without a debit");
            Equal(blank, service.Submit(user, blankId, handwriting), "Retrying a filtered no-text result is also idempotent");
            Equal("no_text", (await Run(Guid.NewGuid(), TextInput(telemetry))).Status, "Local OCR telemetry sent as text is not translated or charged");
            Equal(3, responses.Bodies.Count, "Noise-only inputs never enter the paid translation stage");
            Equal("no_text", (await Run(Guid.NewGuid(), handwriting)).Status, "Uncertainty placeholders alone do not count as readable image text");

            var desktop = await Run(Guid.NewGuid(), image);
            Check(desktop.Status == "succeeded" && desktop.SourceText == document && desktop.Translation == document && desktop.Balance == 6,
                "A non-game desktop document keeps keyboard shortcuts, technical specs and dates in meaningful sentences");
            var failed = await Run(Guid.NewGuid(), TextInput("番号を確認してください。"));
            Check(failed.Status == "failed" && failed.Error == "invalid_model_output" && failed.Balance == 6,
                "When real source is present but the provider returns only telemetry, the unusable translation is not charged");
            foreach (var input in new[] { image, TextInput("hello") })
            {
                var overlong = await Run(Guid.NewGuid(), input);
                Check(overlong.Status == "failed" && overlong.Error == "invalid_model_output" && overlong.Balance == 6,
                    "Raw provider length limits are enforced before text cleanup");
            }
            Equal(0L, fixture.Store.GetAccount(user).Reserved, "Filtering releases every reservation");
            Equal(2, fixture.Store.GetLedger(user).Count(row => row.Kind == "translation"), "Only the two successful content-bearing tasks were charged");
            // Simulate a completed task persisted by an earlier deployment with unfiltered source.
            var legacyId = Guid.NewGuid();
            string key = legacyId.ToString("D");
            fixture.Store.ReserveTranslation(user, key, image.BodyHash(), image.PersistenceJson());
            fixture.Store.StartTranslation(user, key);
            var legacy = new TranslationResult(false, mixed, translated);
            fixture.Store.CompleteTranslation(user, key, JsonSerializer.Serialize(legacy, TranslationInput.Json), charge: true);
            int calls = responses.Bodies.Count;
            var replay = service.Submit(user, legacyId, image);
            Check(replay.SourceText == mixed && replay.Translation == translated && replay.Balance == 5 && calls == responses.Bodies.Count,
                "Pre-upgrade results remain byte-for-byte intact on replay, with no new upstream request or account debit");
        }
        finally { await service.StopAsync(default); }
    }
}
