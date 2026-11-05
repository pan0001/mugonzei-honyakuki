using System.Diagnostics;
using Microsoft.Web.WebView2.Core;
using SteamVrTranslator;

internal static class WebSetupBrowserTests
{
    internal static Task RunAsync() => DesktopResponsivenessTests.OnUiThread(async () =>
    {
        var profile = Path.Combine(Path.GetTempPath(), "mugonzei-setup-web-qa-" + Guid.NewGuid().ToString("N"));
        AppSettings? saved = null;
        int loginCalls = 0;
        using var form = new WebSetupForm(new AppSettings(), settings => saved = settings,
            profileDirectory: profile, authenticate: (server, user, _, _) =>
            {
                if (server != "https://api.mugonzei.top/") throw new InvalidOperationException("First-run sign-in selected the wrong host");
                loginCalls++;
                return Task.FromResult(new CreditSession("synthetic", user, DateTimeOffset.UtcNow.AddMinutes(1), null));
            })
        { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000), ShowInTaskbar = false };
        var surface = form.Controls.OfType<LocalWebSurface>().Single();
        try
        {
            form.Show();
            await WaitFor(async () => surface.CoreForTests is { } core &&
                await core.ExecuteScriptAsync("document.querySelector('#wizard')?.hidden === false") == "true");
            var web = surface.CoreForTests!;
            Task Click(string elementId) => web.ExecuteScriptAsync($"document.getElementById('{elementId}').click()");
            Task Visible(string id) => WaitFor(async () =>
                await web.ExecuteScriptAsync($"document.getElementById('{id}')?.hidden === false") == "true");
            if (await web.ExecuteScriptAsync("document.querySelector('input[name=service]:checked')?.value === 'Credits'") != "true")
                throw new InvalidOperationException("Official account service must be selected on a clean install");
            await Click("next");
            await Visible("account");
            if (await web.ExecuteScriptAsync("document.getElementById('origin').hidden") != "true")
                throw new InvalidOperationException("First-run account login must not display a custom server");
            if (await web.ExecuteScriptAsync("['providers','config','screen'].every(id=>document.getElementById(id).hidden)") != "true")
                throw new InvalidOperationException("Other steps leaked into the sign-in page");
            await Click("register");
            await Visible("verification");
            if (await web.ExecuteScriptAsync("document.getElementById('next').disabled") != "true")
                throw new InvalidOperationException("Registration must await an email verification code");
            await Click("back");
            await WaitFor(async () => await web.ExecuteScriptAsync("document.getElementById('verification').hidden") == "true");
            await web.ExecuteScriptAsync("document.querySelector('#uname').value='test-reader';document.querySelector('#pwd').value='fixture-password'");
            await Click("next");
            await Visible("screen");
            if (await web.ExecuteScriptAsync("document.querySelector('input[name=runtime]:checked').value") != "\"Auto\"")
                throw new InvalidOperationException("Expected auto mode on first run");
            await web.ExecuteScriptAsync("document.querySelector('input[value=Desktop]').click()");
            await WaitFor(async () => await web.ExecuteScriptAsync("document.getElementById('vr-options').hidden") == "true");
            string output = Path.Combine(AppContext.BaseDirectory, "artifacts", "setup-web-preview.png");
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await using (var image = File.Create(output)) await web.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, image);
            await Click("next");
            await WaitFor(() => Task.FromResult(saved != null));
            if (saved?.RuntimeMode != "Desktop" || saved.TranslationBackend != "Credits" || loginCalls != 1 || form.DialogResult != DialogResult.OK)
                throw new InvalidOperationException("The WebView did not save the selected settings");
            using var standalone = new WebSetupForm(new AppSettings { TranslationBackend = "Codex", CreditServerUrl = "https://account.example/" },
                _ => throw new Exception("Standalone login saved settings"), loginOnly: true, profileDirectory: profile,
                authenticate: (_, user, _, _) => Task.FromResult(new CreditSession("synthetic", user, DateTimeOffset.UtcNow.AddMinutes(2), null)))
                { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000), ShowInTaskbar = false };
            standalone.Show();
            var other = standalone.Controls.OfType<LocalWebSurface>().Single();
            await WaitFor(async () => other.CoreForTests is { } core && await core.ExecuteScriptAsync(
                "document.querySelector('#wizard')?.hidden === false && !document.querySelector('#account').hidden && document.querySelector('.progress').hidden") == "true");
            await other.CoreForTests!.ExecuteScriptAsync("document.querySelector('#uname').value='test-reader';document.querySelector('#pwd').value='fixture-password';document.getElementById('next').click()");
            await WaitFor(() => Task.FromResult(standalone.DialogResult == DialogResult.OK));
            Console.WriteLine("PASS WebView2 setup, isolated sign-in, automatic default and saved manual desktop choice");
        }
        finally
        {
            form.Dispose();
            // Edge may keep its isolated profile open briefly while the browser process exits.
            for (int i = 0; i < 10 && Directory.Exists(profile); i++)
            {
                try { Directory.Delete(profile, true); }
                catch (IOException) { await Task.Delay(100); }
                catch (UnauthorizedAccessException) { await Task.Delay(100); }
            }
        }
    });

    private static async Task WaitFor(Func<Task<bool>> predicate)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(22))
        {
            if (await predicate()) return;
            await Task.Delay(70);
        }
        throw new TimeoutException("The embedded setup page did not reach the requested step.");
    }
}
