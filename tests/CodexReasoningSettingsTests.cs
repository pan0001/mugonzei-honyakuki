using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SteamVrTranslator;

public static class CodexReasoningSettingsTests
{
    private static readonly string[] Efforts = ["", "low", "medium", "high"];

    public static async Task RunAsync()
    {
        PersistenceAndMigration();
        await ActualCodexArgumentsAsync();
        await ApiIsolationAsync();
        DraftControlsAndRequestGuard();
        Console.WriteLine("PASS Codex reasoning persistence/migration, all invocation stages, API isolation and unsaved UI guard (no model/VR/network)");
    }

    private static AppSettings Config(string effort = "") => new()
    {
        CodexExecutablePath = Environment.ProcessPath!,
        CodexModel = "gpt-6.1-terra",
        CodexReasoningEffort = effort,
        ProcessingMode = "Handwriting",
        ApiBaseUrl = "https://reasoning-fixture.example/v1",
        ApiModel = "other-provider-model",
        ApiKey = "fixture-key"
    };

    private static void PersistenceAndMigration()
    {
        string directory = Path.Combine(Path.GetTempPath(), "SteamVrTranslator-reasoning-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "settings.json");
        try
        {
            Check(new AppSettings().CodexReasoningEffort == "", "New settings default to the original per-stage strategy");
            foreach (string effort in Efforts)
            {
                var settings = Config(effort); settings.ApiKey = "";
                SettingsStore.Save(settings, directory);
                var loaded = SettingsStore.Load(directory);
                Check(loaded.CodexReasoningEffort == effort && loaded.CodexModel == "gpt-6.1-terra", "Model and explicit/automatic effort survive save/load");
            }
            var document = JsonNode.Parse(File.ReadAllText(path))!;
            document["Settings"]!.AsObject().Remove("CodexReasoningEffort");
            File.WriteAllText(path, document.ToJsonString());
            Check(SettingsStore.Load(directory).CodexReasoningEffort == "", "Missing legacy field retains low/medium/high per-stage behavior");
            foreach (string? invalid in new string?[] { null, "LOW", "ultra", " low", "low\nhigh" })
            {
                var settings = Config(); settings.CodexReasoningEffort = invalid!; settings.ApiKey = "";
                Throws<InvalidOperationException>(() => SettingsStore.Save(settings, directory));
                document["Settings"]!["CodexReasoningEffort"] = invalid;
                File.WriteAllText(path, document.ToJsonString());
                Throws<InvalidOperationException>(() => SettingsStore.Load(directory));
            }
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(directory)) Directory.Delete(directory, false);
        }
    }

    private static async Task ActualCodexArgumentsAsync()
    {
        using var bitmap = new Bitmap(1, 1);
        byte[] png = VisionImage.Prepare(bitmap);
        string[] defaults = ["low", "low", "low", "medium", "high"];
        foreach (string effort in Efforts)
        {
            int calls = 0;
            var client = new CodexTranslationClient((start, _, _) =>
            {
                string[] arguments = start.ArgumentList.ToArray();
                string expected = effort.Length == 0 ? defaults[calls] : effort;
                Check(arguments.Contains("--ignore-user-config"), "App settings are applied independently of personal CLI configuration");
                int modelIndex = Array.IndexOf(arguments, "--model");
                Check(modelIndex >= 0 && arguments[modelIndex + 1] == "gpt-6.1-terra", "Every model call receives the saved Codex model");
                var reasoningArguments = arguments.Where(value => value.StartsWith("model_reasoning_effort=", StringComparison.Ordinal)).ToArray();
                Check(reasoningArguments.Length == 1, "Each invocation receives one unambiguous effort value");
                using var value = JsonDocument.Parse(reasoningArguments[0]["model_reasoning_effort=".Length..]);
                Check(value.RootElement.GetString() == expected, "Explicit effort overrides this stage; automatic retains its own default");
                calls++;
                return Task.FromResult(new CodexProcessResult(0,
                    "{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"fixture\"}}\n{\"type\":\"turn.completed\"}\n", ""));
            });
            var settings = Config(effort);
            await client.TranslateAsync("原文", settings, default);
            await client.AnalyzeImageAsync(png, settings, default);
            await client.RecognizeHandwritingAsync(png, settings, default);
            await client.LocateHandwritingAsync(png, settings, default);
            await client.RecognizeHandwritingViewsAsync([png], "Image 1: whole screenshot.", settings, default);
            Check(calls == defaults.Length, "All Codex execution routes were exercised");
        }

        int invalidCalls = 0;
        var invalidClient = new CodexTranslationClient((_, _, _) => { invalidCalls++; throw new Exception("Invalid configuration launched a process."); });
        foreach (string? effort in new string?[] { null, "LOW", "ultra" })
        {
            var invalid = Config(); invalid.CodexReasoningEffort = effort!;
            await ThrowsAsync<InvalidOperationException>(() => invalidClient.TranslateAsync("原文", invalid, default));
            await ThrowsAsync<InvalidOperationException>(() => invalidClient.AnalyzeImageAsync(png, invalid, default));
            await ThrowsAsync<InvalidOperationException>(() => invalidClient.LocateHandwritingAsync(png, invalid, default));
            await ThrowsAsync<InvalidOperationException>(() => invalidClient.RecognizeHandwritingViewsAsync([png], "Whole image.", invalid, default));
        }
        Check(invalidCalls == 0, "Invalid effort is rejected before starting Codex");
    }

    private static async Task ApiIsolationAsync()
    {
        using var bitmap = new Bitmap(1, 1);
        byte[] png = VisionImage.Prepare(bitmap);
        var originalBodies = new Dictionary<int, string>();
        foreach (string effort in Efforts)
        {
            int calls = 0;
            using var http = new HttpClient(new Handler(async (request, token) =>
            {
                string body = await request.Content!.ReadAsStringAsync(token);
                using var document = JsonDocument.Parse(body);
                Check(!document.RootElement.TryGetProperty("reasoning_effort", out _), "Codex reasoning does not add an API-specific inference parameter");
                Check(!body.Contains("gpt-6.1-terra", StringComparison.Ordinal), "Codex model never replaces the API model");
                if (originalBodies.TryGetValue(calls, out string? original)) Check(original == body, "Changing Codex effort leaves the API request byte-for-byte identical");
                else originalBodies.Add(calls, body);
                calls++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"fixture\"}}]}", Encoding.UTF8, "application/json")
                };
            }));
            using var client = new TranslationClient(http);
            var settings = Config(effort); settings.TranslationBackend = "OpenAI";
            await client.TranslateAsync("原文", settings, default);
            await client.AnalyzeImageAsync(png, settings, default);
            await client.LocateHandwritingAsync(png, settings, default);
            await client.RecognizeHandwritingViewsAsync([png], "Whole image.", settings, default);
            Check(calls == 4, "API text, image, locator and reader routes retain their own configuration");
        }
    }

    private static void DraftControlsAndRequestGuard()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            MainForm? form = null;
            try
            {
                form = new MainForm(true); // Never Show: no hotkeys, polling, VR, or user settings.
                var saved = Field<AppSettings>(form, "settings");
                var control = Field<ComboBox>(form, "codexReasoningEffort");
                using var pixel = new Bitmap(1, 1);
                var invalidSelection = new SelectionCaptureSnapshot(VisionImage.Prepare(pixel), default);
                Check(control.DropDownStyle == ComboBoxStyle.DropDownList && control.Items.Count == 4, "Effort uses the supported fixed choices");
                foreach (string mode in new[] { "Ocr", "Image", "Handwriting", "LocalJapanese" })
                    for (int index = 0; index < Efforts.Length; index++)
                    {
                        saved.ProcessingMode = mode; saved.TranslationBackend = "Codex";
                        saved.CodexReasoningEffort = Efforts[index];
                        Invoke(form, "LoadSettings");
                        Check(control.SelectedIndex == index && control.Enabled && !form.HasPendingAiSettings, "Saved effort maps to its UI choice in every recognition mode");
                        control.SelectedIndex = (index + 1) % Efforts.Length;
                        string draft = Efforts[control.SelectedIndex];
                        Check(form.HasPendingAiSettings && saved.CodexReasoningEffort == Efforts[index], "Changing the dropdown creates a draft without mutating active settings");
                        Check(form.ReadSettingsDraft().CodexReasoningEffort == draft, "Saving reads the exact selected reasoning value");
                        foreach (bool ocrOnly in mode is "Image" or "Handwriting" ? new[] { false, true } : new[] { false })
                        {
                            Field<Label>(form, "status").Text = "";
                            var blocked = (Task)Invoke(form, "CaptureAndTranslate", ocrOnly, "reasoning guard test", 0L, invalidSelection)!;
                            Check(blocked.IsCompleted, "Unsaved effort is blocked before asynchronous capture work");
                            blocked.GetAwaiter().GetResult();
                            Check(Field<Label>(form, "status").Text.Contains("未保存改动", StringComparison.Ordinal), "The request explains that the reasoning change must be saved");
                        }
                        control.SelectedIndex = index;
                        Check(!form.HasPendingAiSettings, "Restoring the saved choice clears the request guard");
                    }
                saved.TranslationBackend = "OpenAI"; saved.CodexReasoningEffort = "low";
                Invoke(form, "LoadSettings");
                Check(!control.Enabled && !form.HasPendingAiSettings, "Codex effort is retained but inactive for other APIs");
                Check(form.ReadSettingsDraft().CodexReasoningEffort == "low", "Saving an API configuration preserves the optional Codex value");
                Check(Field<long>(form, "captureNumber") == 0 && !Field<VrService>(form, "vr").IsConnected, "Guard checks never captured or connected to VR");
            }
            catch (Exception error) { failure = error; }
            finally
            {
                if (form is not null)
                {
                    try { Invoke(form, "DisposeServices"); }
                    finally { form.Dispose(); }
                }
            }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) throw new Exception("Codex reasoning UI checks failed", failure);
    }

    private static T Field<T>(MainForm form, string name) => (T)typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
    private static object? Invoke(MainForm form, string name, params object?[] arguments) => typeof(MainForm).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, arguments);
    private static void Check(bool condition, string message) { if (!condition) throw new Exception("CodexReasoningSettingsTests: " + message); }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
