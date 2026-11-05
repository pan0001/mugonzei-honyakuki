using System.Diagnostics;

namespace SteamVrTranslator;

internal sealed partial class MainForm
{
    private readonly TextBox creditServer = Input();
    private readonly Button creditAccountButton = Button("账号与积分");
    private readonly Label creditBalanceNote = Note("登录、购买次数包、查看订单和消费记录。");
    private UiCard creditSettingsCard = null!;

    private void BuildCreditSettings(TableLayoutPanel stack)
    {
        creditServer.Name = "creditServerUrl";
        creditServer.PlaceholderText = "服务端地址，例如 https://translate.example.com";
        creditAccountButton.Name = "creditAccountButton";
        creditBalanceNote.Name = "creditBalanceNote";
        creditSettingsCard = SettingsCard(stack, "积分服务", "爱发电购买次数包 · 完整翻译成功扣 1 积分");
        SettingRow(creditSettingsCard, "服务地址", creditServer);
        SettingRow(creditSettingsCard, "账号管理", creditAccountButton);
        SettingRow(creditSettingsCard, "", creditBalanceNote);
        creditAccountButton.Click += (_, _) => TryAction(OpenCreditAccount);
    }

    private void OpenCreditAccount() => OpenAccountPage(loginOnly: false);

    private void OpenAccountPage(bool loginOnly)
    {
        if (closing || setupDialogOpen) return;
        if (busy || selectionActive || selectionCapture is not null || loginCheck is not null
            || apiVisionCheck is not null || googleTranslationCheck is not null)
            throw new InvalidOperationException("请先完成或取消当前任务，再管理账号与积分。");
        string destination = CreditServiceClient.NormalizeServerUrl(creditServer.Text.Trim());
        if (destination != CreditServiceClient.NormalizeServerUrl(settings.CreditServerUrl))
            throw new InvalidOperationException("积分服务器地址已修改，请先保存偏好，再登录此服务器。");
        bool resumePolling = pollTimer.Enabled;
        setupDialogOpen = true;
        pollTimer.Stop();
        try
        {
            vr.ResetTranslationInput();
            TryAction(HideHoldOverlay);
            var draft = ReadSettingsDraft();
            if (loginOnly || string.IsNullOrEmpty(ReadWebAccountName()))
            {
                using var login = new WebSetupForm(draft, _ => { }, firstRun: false, startAtLogin: true,
                    profileDirectory: web?.ProfileDirectory, loginOnly: true);
                if (login.ShowDialog(this) != DialogResult.OK || loginOnly) return;
            }
            using var account = new WebCreditAccountForm(destination);
            account.ShowDialog(this);
            creditBalanceNote.Text = "账户中心已关闭。";
        }
        finally
        {
            try { vr.ResetTranslationInput(); }
            finally
            {
                setupDialogOpen = false;
                if (!closing && resumePolling) pollTimer.Start();
            }
        }
    }

    private async Task RunCreditTranslationAsync(CaptureRequest request, Stopwatch started,
        string? text, byte[]? png, CancellationToken token)
    {
        using var client = new CreditServiceClient(settings.CreditServerUrl);
        string mode = png is null ? "text" : settings.ProcessingMode == "Handwriting" ? "handwriting" : "image";
        SetStatus("正在通过积分服务识别与翻译…完整任务成功后扣 1 积分。");
        ShowStatus("正在翻译…");
        // A single ID survives HTTP retries and recovery; never bill each OCR stage separately.
        var result = await client.ExecuteAsync(Guid.NewGuid(), mode, text, png, settings.TargetLanguage, token);
        token.ThrowIfCancellationRequested();
        creditBalanceNote.Text = $"账户积分余额：{result.Balance} · 可在「账号与积分」查看可用与预留积分。";
        var original = result.SourceText ?? text;
        sourceText.Text = Mugonzei.Shared.GameTextFilter.Clean(original);
        if (result.Status == "no_text")
        {
            SetStatus("没有识别到可读文字，本次不扣积分。请靠近文字或缩小截图范围。");
            ShowStatus("未识别到文字 · 未扣积分");
            return;
        }
        if (result.Status == "cancelled") throw new OperationCanceledException("服务端已取消本次任务。");
        if (result.Status != "succeeded" || string.IsNullOrWhiteSpace(result.Translation))
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error)
                ? "积分服务尚未返回有效结果，请到「账号与积分」恢复最近任务。" : result.Error);
        var translation = Mugonzei.Shared.GameTextFilter.Clean(result.Translation);
        if (translation.Length == 0 || (!string.IsNullOrWhiteSpace(original) && sourceText.Text.Length == 0))
        {
            // Older services may already have charged for a response containing only interface text.
            SetStatus($"没有可显示的正文 · 积分服务已完成此任务 · 当前余额 {result.Balance} 分");
            ShowStatus("没有可显示的正文"); return;
        }
        translatedText.Text = translation;
        await PublishResultPagesAsync(translation, request.GestureId, token, settings.ShowSource ? sourceText.Text : null);
        SetStatus($"翻译完成 · 扣除 1 积分 · 余额 {result.Balance} 分 · {started.Elapsed.TotalSeconds:F1} 秒 · {pages.Count} 页");
    }
}
