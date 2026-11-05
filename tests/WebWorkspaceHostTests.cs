using Microsoft.Web.WebView2.Core;
using SteamVrTranslator;
using System.Reflection;
using System.Text.Json;

internal static class WebWorkspaceHostTests
{
    internal static async Task RunAsync()
    {
        var existing = new AppSettings { ApiBaseUrl = "https://old.invalid/v1", ApiKey = "synthetic-never-return" };
        using var patch = JsonDocument.Parse("""{"apiBaseUrl":"https://new.invalid/v1"}""");
        var next = WebSettingsPatch.Apply(existing, patch.RootElement);
        if (next.ApiKey != "" || existing.ApiKey.Length == 0 || WebSettingsPatch.Public(existing).ToJsonString().Contains(existing.ApiKey))
            throw new Exception("Workspace bridge leaked or reused an upstream key");
        string profile = Path.Combine(Path.GetTempPath(), "Mugonzei-Web-Test-" + Guid.NewGuid().ToString("N"));
        int before = SteamVrPresence.ReadServerPid();
        try
        {
            await DesktopResponsivenessTests.OnUiThread(async () =>
            {
                var published = new Uri("https://github.com/pan0001/mugonzei-honyakuki/releases/tag/v90.0.0");
                var installer = new Uri("https://github.com/pan0001/mugonzei-honyakuki/releases/download/v90.0.0/Mugonzei-Honyakuki-Setup-win-x64-v90.0.0.exe");
                var check = new TaskCompletionSource<GitHubUpdateResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                int requests = 0;
                Uri? opened = null;
                using var form = new MainForm(true, useWebUi: true, webProfileDirectory: profile,
                    checkForUpdates: ct => { requests++; return check.Task.WaitAsync(ct); },
                    openUpdateLink: uri => opened = uri) {
                    ShowInTaskbar = false, StartPosition = FormStartPosition.Manual,
                    Location = new Point(-15000, -15000), Size = new Size(1130, 760)
                };
                form.Show();
                var surface = form.Controls.OfType<LocalWebSurface>().Single();
                await Until(() => Task.FromResult(surface.CoreForTests is { } c && surface.IsEntry(c.Source)));
                var core = surface.CoreForTests!;
                Task<string> Js(string script) => core.ExecuteScriptAsync(script);
                Task WaitJs(string script) => Until(async () => await Js(script) == "true");
                await WaitJs("document.querySelector('#activeMode')?.textContent.includes('桌面') === true");
                if (requests != 0) throw new Exception("Update checks should only run on request");
                await Js("document.querySelector('#updateEntry').click()");
                await Until(() => Task.FromResult(requests == 1));
                await Js("window.chrome.webview.postMessage({type:'action',action:'checkUpdate'})");
                await WaitJs("document.querySelector('#updatePanel').innerText.includes('GitHub')");
                check.SetResult(new GitHubUpdateResult(true, GitHubUpdateChecker.CurrentVersion, "90.0.0", published, installer));
                await WaitJs("document.querySelector('#updateDownload').getClientRects().length > 0");
                if (requests != 1 || opened is not null) throw new Exception("An update check repeated or opened a download without a click");
                await Js("document.querySelector('#updateDownload').click()");
                await Until(() => Task.FromResult(opened == installer));
                await Js("document.querySelector('#updateClose').click()");
                await Js("document.querySelector('input[name=mode][value=Desktop]').click()");
                await Until(() => Task.FromResult(form.ReadSettingsDraft().RuntimeMode == "Desktop"));
                await Js("window.sameControl = document.querySelector('input[name=mode][value=Desktop]'); window.sameControl.focus()");
                var setStatus = typeof(MainForm).GetMethod("SetStatus", BindingFlags.Instance | BindingFlags.NonPublic)!;
                for (int i = 0; i < 12; i++) { setStatus.Invoke(form, ["界面刷新样本 " + i]); await Task.Delay(14); }
                await WaitJs("document.activeElement === window.sameControl && document.querySelector('input[name=mode][value=Desktop]') === window.sameControl && document.body.innerText.includes('界面刷新样本 11')");
                if (core.Settings.AreHostObjectsAllowed || core.Settings.IsPasswordAutosaveEnabled || surface.IsEntry("https://example.com/workspace.html"))
                    throw new Exception("privileged bridge origin or browser settings");
                string output = Path.Combine(AppContext.BaseDirectory,"artifacts");
                Directory.CreateDirectory(output);
                using(var image = File.Create(Path.Combine(output,"embedded-workspace.png")))
                    await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,image);
                var denial = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                core.NavigationStarting += (_, e) => { if (e.Uri.StartsWith("https://outside.invalid/")) denial.TrySetResult(e.Cancel); };
                core.Navigate("https://outside.invalid/");
                if (!await denial.Task.WaitAsync(TimeSpan.FromSeconds(3))) throw new Exception("Navigation escaped the local workspace");
                await Js("document.querySelector('input[name=mode][value=Auto]').click()");
                await Until(() => Task.FromResult(form.ReadSettingsDraft().RuntimeMode == "Auto"));
                form.Close();
            });
            if (before == 0 && SteamVrPresence.ReadServerPid() != 0) throw new Exception("SteamVR appeared during a desktop-only smoke test");
            Console.WriteLine("PASS real embedded workspace, mode bridge, stable focused control through live updates, isolated credentials and blocked navigation");
        }
        finally
        {
            for (int i = 0; i < 10 && Directory.Exists(profile); i++)
            {
                try { Directory.Delete(profile, true); break; }
                catch (IOException) { await Task.Delay(220); }
                catch (UnauthorizedAccessException) { await Task.Delay(220); }
            }
        }
    }

    private static async Task Until(Func<Task<bool>> condition)
    {
        long until = Environment.TickCount64 + 18000;
        while (!await condition()) { if (Environment.TickCount64 >= until) throw new TimeoutException("Embedded workspace did not reach expected state"); await Task.Delay(70); }
    }
}
