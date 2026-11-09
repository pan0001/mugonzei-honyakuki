using System.Reflection;
using SteamVrTranslator;

public static class ApiSettingsUiTests
{
    public static void Run()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            MainForm? form = null;
            try
            {
                form = new MainForm(true);
                var preset = Get<ComboBox>(form, "apiPreset");
                var endpoint = Get<TextBox>(form, "endpoint");
                var key = Get<TextBox>(form, "apiKey");
                var model = Get<TextBox>(form, "model");
                preset.SelectedIndex = ApiProviderPresets.All.ToList().FindIndex(item => item.Name == "OpenRouter");
                key.Text = "old-provider-test-key"; model.Text = "old-provider-model";
                Apply(form);
                Check(key.Text.Length == 0 && model.Text.Length == 0, "Switching provider must clear the previous provider's credentials/model");
                Check(endpoint.Text == "https://openrouter.ai/api/v1", "Selected preset writes its exact endpoint");
                Check(Get<ComboBox>(form, "backend").SelectedIndex == 1, "Preset selects the other API backend");
                endpoint.Text = "https://openrouter.ai/custom/v1";
                key.Text = "same-provider-test-key"; model.Text = "same-provider-model";
                Apply(form);
                Check(key.Text == "same-provider-test-key" && model.Text == "same-provider-model", "Changing only a path preserves same-provider credentials");
                Check(form.HasPendingAiSettings, "A preset is a draft until explicitly saved");
                preset.SelectedIndex = 0;
                Apply(form);
                Check(endpoint.Text == "https://openrouter.ai/api/v1" && key.Text == "same-provider-test-key", "Custom preset preserves supplied settings");
                Get<Label>(form, "apiTestResult").Text = "图片测试通过";
                endpoint.Text = "https://api.siliconflow.cn/v1";
                Check(key.Text.Length == 0, "Manually changing authority must also clear credentials from the old destination");
                Check(Get<Label>(form, "apiTestResult").Text.Contains("配置已变更"), "Changing draft configuration invalidates old success");
                preset.SelectedIndex = 1;
                Apply(form);
                Check(key.Text.Length == 0, "Applying a preset after manually changing the endpoint cannot restore old credentials");
                key.Text = "new-provider-test-key";
                Apply(form);
                Check(key.Text == "new-provider-test-key", "New credentials entered for the current destination remain usable");
                Get<Label>(form, "apiTestResult").Text = "图片测试通过";
                model.Text = "a-different-vision-model";
                Check(Get<Label>(form, "apiTestResult").Text.Contains("配置已变更"), "Old model success does not imply a new model was verified");
                Get<ComboBox>(form, "backend").SelectedItem = "Google 搜索 AI 模式（实验性）";
                var google = form.ReadSettingsDraft();
                Check(google.TranslationBackend == "GoogleSearchAI" && google.ProcessingMode == "Image" && !Get<ComboBox>(form, "processingMode").Enabled,
                    "The native service picker selects direct screenshots for Search AI");
                Check(google.ApiKey == "new-provider-test-key" && Get<Label>(form, "dataNote").Text.Contains("截图和目标语言会发送给 Google"),
                    "Switching to Search AI preserves other credentials and discloses the screenshot destination");
                Get<ComboBox>(form, "backend").SelectedIndex = 2;
                Check(form.ReadSettingsDraft() is { TranslationBackend: "GoogleTranslate", ProcessingMode: "Ocr" } && Get<ComboBox>(form, "processingMode").Enabled,
                    "Switching back to the text-only Google translation service selects local OCR");
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                if (form is not null)
                {
                    typeof(MainForm).GetMethod("DisposeServices", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, null);
                    form.Dispose();
                }
            }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) throw new Exception("API settings UI test failed", failure);
        Console.WriteLine("PASS API preset destination, credential isolation and pending settings UI");
    }
    private static void Apply(MainForm form) => typeof(MainForm).GetMethod("ApplyApiPreset", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, null);
    private static T Get<T>(MainForm form, string name) => (T)typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
    private static void Check(bool success, string message) { if (!success) throw new Exception(message); }
}
