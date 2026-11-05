using System.Globalization;
using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;

namespace Mugonzei.Server;

public sealed record EmailCodeRequest(string? Email);
public sealed record RegistrationRequest(string? Username, string? Password, string? Email = null, string? Code = null);
internal sealed record PresentedEmailCode(string Email, string EmailKey, byte[] Digest);

public sealed class RegistrationEmailOptions
{
    public string Host { get; init; } = "";
    public int Port { get; init; }
    public string Username { get; init; } = "";
    public string Password { get; init; } = "";
    public string From { get; init; } = "";
    public byte[] CodeKey { get; init; } = [];
    public int DailyLimit { get; init; } = 200;
    public long? TotalLimit { get; init; }
    private bool UsesDirectMail
    {
        get
        {
            string host = Host.TrimEnd('.');
            return host.StartsWith("smtpdm", StringComparison.OrdinalIgnoreCase)
                && (host.EndsWith(".aliyuncs.com", StringComparison.OrdinalIgnoreCase)
                    || host.EndsWith(".aliyun.com", StringComparison.OrdinalIgnoreCase));
        }
    }
    internal int EffectiveDailyLimit => UsesDirectMail ? Math.Min(DailyLimit, 200) : DailyLimit;
    public bool Ready => Host.Length > 0 && Port is >= 1 and <= 65535 and not 465 && Username.Length > 0
        && Password.Length > 0 && MailAddress.TryCreate(From, out var sender) && sender.Address == From && CodeKey.Length == 32
        && DailyLimit is >= 1 and <= 100_000
        && (UsesDirectMail ? TotalLimit is >= 0 and <= 2000 : TotalLimit is null or >= 0);

    public static RegistrationEmailOptions Load(IConfiguration config)
    {
        byte[] key;
        try { key = Convert.FromBase64String(config["MUGONZEI_EMAIL_CODE_KEY"] ?? ""); }
        catch (FormatException) { key = []; }
        long? totalLimit = null;
        if (config["MUGONZEI_EMAIL_TOTAL_LIMIT"] is string total)
            totalLimit = long.TryParse(total, NumberStyles.None, CultureInfo.InvariantCulture, out long count) ? count : -1;
        return new()
        {
            Host = (config["MUGONZEI_SMTP_HOST"] ?? "").Trim(),
            Port = int.TryParse(config["MUGONZEI_SMTP_PORT"] ?? "587", out var port) ? port : 0,
            Username = config["MUGONZEI_SMTP_USERNAME"] ?? "", Password = config["MUGONZEI_SMTP_PASSWORD"] ?? "",
            From = (config["MUGONZEI_SMTP_FROM"] ?? "").Trim(), CodeKey = key,
            DailyLimit = int.TryParse(config["MUGONZEI_EMAIL_DAILY_LIMIT"] ?? "200", out int limit) ? limit : 0,
            TotalLimit = totalLimit
        };
    }
}

public interface IRegistrationMailer
{
    Task SendAsync(string address, string? code, CancellationToken cancellation);
}

public sealed class SmtpRegistrationMailer(RegistrationEmailOptions options) : IRegistrationMailer
{
    public async Task SendAsync(string address, string? code, CancellationToken cancellation)
    {
        using var message = new MailMessage(new MailAddress(options.From, "无言势翻译器", Encoding.UTF8), new MailAddress(address))
        {
            Subject = "无言势翻译器 · 注册验证码", SubjectEncoding = Encoding.UTF8, BodyEncoding = Encoding.UTF8,
            Body = code is null
                ? "此邮箱已注册无言势翻译器，可使用原用户名和密码登录。如果你没有申请注册，可以忽略这封邮件。"
                : $"你的注册验证码是 {code}，10 分钟内有效。请勿将验证码告诉他人。如果你没有注册，可以忽略这封邮件。"
        };
        using var smtp = new SmtpClient(options.Host, options.Port)
        {
            DeliveryMethod = SmtpDeliveryMethod.Network, EnableSsl = true, UseDefaultCredentials = false,
            Credentials = new NetworkCredential(options.Username, options.Password)
        };
        await smtp.SendMailAsync(message, cancellation);
    }
}

public sealed class RegistrationEmailService(Store store, RegistrationEmailOptions options, IRegistrationMailer mailer, TimeProvider clock)
{
    public static string NormalizeAddress(string? input)
    {
        string value = input?.Trim() ?? "";
        if (value.Length is < 5 or > 254 || value.Any(c => !char.IsAscii(c) || char.IsWhiteSpace(c) || char.IsControl(c))
            || !MailAddress.TryCreate(value, out var parsed) || parsed.Address != value || parsed.User.Length > 64
            || !parsed.User.All(c => char.IsAsciiLetterOrDigit(c) || ".!#$%&'*+-/=?^_`{|}~".Contains(c))
            || parsed.User.StartsWith('.') || parsed.User.EndsWith('.') || parsed.User.Contains("..")
            || !parsed.Host.Contains('.') || parsed.Host.Split('.').Any(label => label.Length is < 1 or > 63
                || label.StartsWith('-') || label.EndsWith('-') || label.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')))
            throw new ApiException(400, "invalid_email", "请输入有效的邮箱地址。");
        return value.ToLowerInvariant();
    }

    public async Task RequestAsync(string? input, IPAddress? source, CancellationToken cancellation)
    {
        string email = NormalizeAddress(input);
        EnsureReady();
        string code = RandomNumberGenerator.GetInt32(1_000_000).ToString("D6", CultureInfo.InvariantCulture);
        string emailKey = AddressKey(email);
        string delivery = Guid.NewGuid().ToString("N");
        string ipKey = Convert.ToHexString(Digest("source", SourceGroup(source)));
        bool issueCode = store.ReserveEmailDelivery(email, emailKey, ipKey, delivery, CodeHash(email, code),
            clock.GetUtcNow(), options.EffectiveDailyLimit, options.TotalLimit);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            // Existing addresses receive a sign-in reminder through the same transport, so latency and
            // relay failures do not reveal whether somebody has an account at this address.
            await mailer.SendAsync(email, issueCode ? code : null, deadline.Token);
            if (issueCode) store.FinishEmailDelivery(emailKey, delivery, delivered: true);
        }
        catch (Exception ex) when (ex is SmtpException or OperationCanceledException or IOException or InvalidOperationException or System.Net.Sockets.SocketException)
        {
            if (issueCode) store.FinishEmailDelivery(emailKey, delivery, delivered: false);
            if (cancellation.IsCancellationRequested) throw new OperationCanceledException(cancellation);
            throw Unavailable();
        }
    }

    internal PresentedEmailCode VerifyBeforeHashing(RegistrationRequest input)
    {
        if (string.IsNullOrWhiteSpace(input.Email) || string.IsNullOrEmpty(input.Code))
            throw new ApiException(400, "email_verification_required", "新账户需要邮箱验证码，请更新客户端或前往网站注册。");
        string email = NormalizeAddress(input.Email);
        if (input.Code.Length != 6 || input.Code.Any(c => !char.IsAsciiDigit(c))) throw InvalidCode();
        EnsureReady();
        var presented = new PresentedEmailCode(email, AddressKey(email), CodeHash(email, input.Code));
        store.VerifyRegistrationCode(presented, clock.GetUtcNow());
        return presented;
    }

    internal string Register(PresentedEmailCode code, string username, byte[] passwordHash, byte[] salt, int iterations) =>
        store.RegisterVerifiedUser(code, clock.GetUtcNow(), username, passwordHash, salt, iterations);

    private byte[] Digest(string scope, string value) =>
        HMACSHA256.HashData(options.CodeKey, Encoding.UTF8.GetBytes(scope + "\0" + value));
    private string AddressKey(string email) => Convert.ToHexString(Digest("address", email));
    private byte[] CodeHash(string email, string code) => Digest("registration-code", email + "\0" + code);
    private void EnsureReady() { if (!options.Ready) throw Unavailable(); }
    private static ApiException Unavailable() => new(503, "email_unavailable", "邮箱验证服务暂时不可用，请稍后重试。");
    internal static ApiException InvalidCode() => new(400, "invalid_email_code", "验证码错误、已过期或已使用，请检查或重新获取。");

    private static string SourceGroup(IPAddress? address)
    {
        if (address is null) return "unknown";
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        if (bytes.Length == 16) Array.Clear(bytes, 8, 8);
        return Convert.ToHexString(bytes);
    }
}
