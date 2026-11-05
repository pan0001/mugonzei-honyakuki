namespace SteamVrTranslator;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        using var instance = new Mutex(true, "Local\\SteamVrTranslator.Desktop", out var first);
        if (!first)
        {
            MessageBox.Show("无言势翻译器已经在运行，请从系统托盘打开。", "无言势翻译器");
            return;
        }
        if (args.Contains("--vr-smoke"))
        {
            Diagnostics.Run();
            return;
        }
        if (args.Contains("--hold-smoke"))
        {
            HoldOverlayDiagnostics.Run();
            return;
        }
        if (args.Contains("--codex-smoke"))
        {
            Diagnostics.RunCodexAsync().GetAwaiter().GetResult();
            return;
        }
        if (args.Contains("--vision-smoke"))
        {
            Diagnostics.RunVisionAsync().GetAwaiter().GetResult();
            return;
        }
        if (args.Contains("--handwriting-smoke"))
        {
            HandwritingDiagnostics.RunAsync().GetAwaiter().GetResult();
            return;
        }
        if (args.Contains("--recognition-smoke"))
        {
            int imageArgument = Array.IndexOf(args, "--image");
            string? imagePath = imageArgument >= 0 && imageArgument + 1 < args.Length ? args[imageArgument + 1] : null;
            RecognitionQualityDiagnostics.RunAsync(imagePath).GetAwaiter().GetResult();
            return;
        }
        if (args.Contains("--handwriting-benchmark"))
        {
            int manifestArgument = Array.IndexOf(args, "--manifest");
            string? manifestPath = manifestArgument >= 0 && manifestArgument + 1 < args.Length ? args[manifestArgument + 1] : null;
            HandwritingBenchmark.RunAsync(manifestPath).GetAwaiter().GetResult();
            return;
        }
        if (args.Contains("--local-ocr-smoke"))
        {
            int imageArgument = Array.IndexOf(args, "--image");
            string? imagePath = imageArgument >= 0 && imageArgument + 1 < args.Length ? args[imageArgument + 1] : null;
            LocalJapaneseDiagnostics.RunAsync(imagePath, args.Contains("--translate")).GetAwaiter().GetResult();
            return;
        }
        if (args.Contains("--google-web-smoke"))
        {
            GoogleWebDiagnostics.RunAsync().GetAwaiter().GetResult();
            return;
        }
        if (args.Contains("--google-screenshot-smoke"))
        {
            int imageArgument = Array.IndexOf(args, "--image");
            string? imagePath = imageArgument >= 0 && imageArgument + 1 < args.Length ? args[imageArgument + 1] : null;
            GoogleScreenshotDiagnostics.RunAsync(imagePath, args.Contains("--ocr-only")).GetAwaiter().GetResult();
            return;
        }
        bool demo = args.Contains("--demo");
        if (!demo)
        {
            bool firstRun = SettingsStore.NeedsInitialSetup();
            AppSettings? existing = null;
            if (!firstRun)
            {
                try { existing = SettingsStore.Load(); }
                catch (InvalidOperationException) { /* MainForm can display and recover damaged settings. */ }
            }
            bool signIn = existing is not null && NeedsAccountSignIn(existing, new CreditSessionStore().Load);
            bool configure = args.Contains("--setup");
            if (firstRun || signIn || configure)
            {
                using var setup = new SetupWizardForm(existing ?? new AppSettings(), settings => SettingsStore.Save(settings),
                    firstRun: firstRun, startAtLogin: signIn && !configure)
                    { StartPosition = FormStartPosition.CenterScreen, ShowInTaskbar = true };
                if (setup.ShowDialog() != DialogResult.OK) return;
            }
        }
        Application.Run(new MainForm(demo));
    }

    internal static bool NeedsAccountSignIn(AppSettings settings, Func<string, CreditSession?> load)
    {
        if (settings.TranslationBackend != "Credits") return false;
        try { return load(settings.CreditServerUrl) is not { } session || session.ExpiresAt <= DateTimeOffset.UtcNow; }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException) { return true; }
    }
}
