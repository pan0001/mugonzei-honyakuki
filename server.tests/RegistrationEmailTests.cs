using System.Net;
using System.Net.Mail;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Mugonzei.Server;

internal static partial class Program
{
    private sealed class RegistrationClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan amount) => now += amount;
    }

    private sealed class TestMailbox : IRegistrationMailer
    {
        public int Sends { get; private set; }
        public string LastCode { get; private set; } = "";
        public bool Fail { get; set; }
        public Task SendAsync(string address, string? code, CancellationToken token)
        {
            if (!address.EndsWith("@example.test", StringComparison.Ordinal)) throw new Exception("Unexpected fixture recipient");
            Sends++; LastCode = code ?? "";
            if (Fail) throw new SmtpException("Private synthetic transport diagnostic");
            return Task.CompletedTask;
        }
    }

    private static RegistrationEmailOptions TestEmailOptions(int dailyLimit = 200, string host = "smtp.example.test", int port = 587) => new()
    {
        Host = host, Port = port, Username = "smtp-fixture", Password = "synthetic-mail-credential",
        From = "noreply@example.test", CodeKey = RandomNumberGenerator.GetBytes(32), DailyLimit = dailyLimit
    };

    // This host lives exclusively in the test assembly. Only the mail transport is replaced.
    private sealed class FileTestInbox(string path) : IRegistrationMailer
    {
        public Task SendAsync(string address, string? code, CancellationToken token)
        {
            if (address != "smoke@example.test") throw new SmtpException("Unknown test recipient");
            return File.WriteAllTextAsync(path, code ?? "already registered", new UTF8Encoding(false), token);
        }
    }
    private static async Task<int> ServeEmailFixture(string[] args)
    {
        if (args.Length != 4 || args[2] != "--urls" || !Uri.TryCreate(args[3], UriKind.Absolute, out var uri)
            || uri.Scheme != "http" || uri.Host != "127.0.0.1" || uri.Port <= 0
            || Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") != "Development"
            || Environment.GetEnvironmentVariable("MUGONZEI_MOCK_PAYMENTS") != "true")
            throw new ArgumentException("Email integration fixture requires a loopback development test host");
        string inbox = Path.GetFullPath(args[1]);
        string relative = Path.GetRelativePath(Path.GetTempPath(), inbox);
        if (!Path.IsPathFullyQualified(args[1]) || Path.IsPathRooted(relative) || relative.StartsWith("..")
            || File.Exists(inbox) || !Directory.Exists(Path.GetDirectoryName(inbox)))
            throw new ArgumentException("Email integration fixture requires a fresh inbox inside the temp directory");
        await MugonzeiHost.RunAsync(args[2..], services =>
        {
            services.AddSingleton(TestEmailOptions()); services.AddSingleton<IRegistrationMailer>(new FileTestInbox(inbox));
        });
        return 0;
    }

    private static async Task RegistrationEmailTests()
    {
        using var fixture = new Fixture();
        var clock = new RegistrationClock();
        var box = new TestMailbox();
        var settings = TestEmailOptions();
        var email = new RegistrationEmailService(fixture.Store, settings, box, clock);
        var auth = new AuthService(fixture.Store);
        var ip = IPAddress.Loopback;
        const string address = "sender@example.test";
        const string password = "correct-password-1234";
        RegistrationRequest Attempt(string code, string user = "mail_fixture") => new(user, password, address, code);
        async Task Send() => await email.RequestAsync(address, ip, default);

        foreach (string invalid in new[] { "", "a@localhost", "Alice <a@example.test>", "a@x..test", "a..b@example.test", "a@x.test\r\nBcc:b@test.org" })
            Throws(400, () => RegistrationEmailService.NormalizeAddress(invalid));
        var disabled = new RegistrationEmailService(fixture.Store, new RegistrationEmailOptions(), box, clock);
        await ThrowsApiAsync(503, () => disabled.RequestAsync(address, ip, default));
        Throws(503, () => auth.Register(Attempt("012345"), disabled));
        Throws(400, () => auth.Register(new("mail_fixture", password), email));
        Throws(400, () => auth.Register(Attempt("12345"), email));

        await Send();
        string first = box.LastCode;
        Check(first.Length == 6 && first.All(char.IsAsciiDigit), "Issued code is six ASCII digits");
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = fixture.Path }.ToString()))
        {
            db.Open();
            using var query = db.CreateCommand();
            query.CommandText = "SELECT email_key,code_hash FROM registration_email_challenges";
            using var row = query.ExecuteReader();
            Check(row.Read() && !row.GetString(0).Contains("@") && ((byte[])row[1]).Length == 32 &&
                !((byte[])row[1]).SequenceEqual(SHA256.HashData(Encoding.UTF8.GetBytes(first))),
                "Database stores a keyed digest for the low-entropy code and an opaque pending address");
        }
        await ThrowsApiAsync(429, Send);
        string wrong = first == "000000" ? "000001" : "000000";
        for (int i=0; i<5; i++) Throws(400, () => auth.Register(Attempt(wrong), email));
        var afterRestart = new RegistrationEmailService(new Store(fixture.Path), settings, box, clock);
        Throws(400, () => auth.Register(Attempt(first), afterRestart));
        Check(fixture.Store.FindUser("mail_fixture") is null, "Wrong and spent attempts create no user even after restart");
        clock.Advance(TimeSpan.FromSeconds(60));
        await Send();
        var fresh = Attempt(box.LastCode);
        if (fresh.Code != first) Throws(400, () => auth.Register(Attempt(first), email));
        Throws(400, () => auth.Register(fresh with { Email = "different@example.test" }, email));
        clock.Advance(TimeSpan.FromMinutes(10));
        Throws(400, () => auth.Register(fresh, email));
        box.Fail = true;
        await ThrowsApiAsync(503, Send);
        Throws(400, () => auth.Register(Attempt(box.LastCode), email));
        await ThrowsApiAsync(429, Send);
        box.Fail = false;
        clock.Advance(TimeSpan.FromMinutes(1));
        await Send();
        string delivered = box.LastCode;
        fixture.LegacyUser(new("occupied_name", password));
        Throws(409, () => auth.Register(Attempt(delivered, "occupied_name"), email));
        var winners = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => Task.Run(() =>
        {
            try { return auth.Register(Attempt(delivered, "competing_" + i) with { Email = " Sender@EXAMPLE.TEST " }, afterRestart).Username; }
            catch (ApiException error) when (error.Code == "invalid_email_code") { return null; }
        })));
        Equal(1, winners.Count(name => name != null), "Concurrent verified submissions create exactly one account");
        string winner = winners.Single(name => name != null)!;
        Equal(winner, auth.Login(new(winner, password)).Username, "Verified account can log in with the original login contract");
        Equal("occupied_name", auth.Login(new("occupied_name", password)).Username, "Existing account without an email remains usable");
        clock.Advance(TimeSpan.FromMinutes(1));
        int sendCount = box.Sends;
        await Send();
        Equal(sendCount + 1, box.Sends, "Bound addresses use the same delivery path as new addresses");
        Equal("", box.LastCode, "Existing account reminder contains no usable code");
        Throws(400, () => auth.Register(Attempt(delivered, "replay_user"), email));
        clock.Advance(TimeSpan.FromMinutes(1));
        box.Fail = true;
        await ThrowsApiAsync(503, Send);

        await RegistrationQuotas();
        await RegistrationEmailBudget();
        await RefusePlainSmtp();
    }

    private static async Task RegistrationQuotas()
    {
        using var f = new Fixture();
        var c = new RegistrationClock();
        var b = new TestMailbox();
        var options = TestEmailOptions(dailyLimit: 22);
        var service = new RegistrationEmailService(f.Store, options, b, c);
        var ip = IPAddress.Parse("2001:db8:1234:5678::1");
        for (int i=0; i<6; i++)
        {
            await service.RequestAsync("repeat@example.test", ip, default);
            c.Advance(TimeSpan.FromSeconds(61));
        }
        await ThrowsApiAsync(429, () => service.RequestAsync("repeat@example.test", ip, default));
        for (int i=0; i<14; i++) await service.RequestAsync($"user{i}@example.test", ip, default);
        await ThrowsApiAsync(429, () => service.RequestAsync("ipcap@example.test", IPAddress.Parse("2001:db8:1234:5678::99"), default));
        var nextProcess = new RegistrationEmailService(new Store(f.Path), options, b, c);
        await nextProcess.RequestAsync("other@example.test", IPAddress.Loopback, default);
        await nextProcess.RequestAsync("another@example.test", IPAddress.Loopback, default);
        await ThrowsApiAsync(429, () => nextProcess.RequestAsync("capped@example.test", IPAddress.Loopback, default));
        Equal(22, b.Sends, "Durable address, source and global quotas bound outbound mail even across restarts");
        c.Advance(TimeSpan.FromDays(1));
        await nextProcess.RequestAsync("repeat@example.test", ip, default);
        Equal(23, b.Sends, "Rolling quota expires without an operator reset");
    }

    private static async Task RegistrationEmailBudget()
    {
        using var f = new Fixture();
        var clock = new RegistrationClock();
        var mailbox = new TestMailbox();
        var config = new ConfigurationManager
        {
            ["MUGONZEI_SMTP_HOST"] = "SMTPDM-ap-southeast-1.aliyuncs.com.",
            ["MUGONZEI_SMTP_PORT"] = "80",
            ["MUGONZEI_SMTP_FROM"] = "noreply@example.test",
            ["MUGONZEI_SMTP_USERNAME"] = "fixture",
            ["MUGONZEI_SMTP_PASSWORD"] = "fixture",
            ["MUGONZEI_EMAIL_CODE_KEY"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            ["MUGONZEI_EMAIL_DAILY_LIMIT"] = "500"
        };
        foreach (string? invalid in new string?[] { null, "", "-1", "abc", "1.5", "2001", "9223372036854775808" })
        {
            config["MUGONZEI_EMAIL_TOTAL_LIMIT"] = invalid;
            var disabled = new RegistrationEmailService(f.Store, RegistrationEmailOptions.Load(config), mailbox, clock);
            await ThrowsApiAsync(503, () => disabled.RequestAsync("check@example.test", IPAddress.Loopback, default));
        }
        config["MUGONZEI_EMAIL_TOTAL_LIMIT"] = "0";
        var paused = new RegistrationEmailService(f.Store, RegistrationEmailOptions.Load(config), mailbox, clock);
        await ThrowsApiAsync(503, () => paused.RequestAsync("check@example.test", IPAddress.Loopback, default));
        Equal(0, mailbox.Sends, "DirectMail requires an explicit positive allowance before contacting SMTP");

        config["MUGONZEI_SMTP_HOST"] = "smtp.example.test";
        config["MUGONZEI_EMAIL_TOTAL_LIMIT"] = "3000";
        Check(RegistrationEmailOptions.Load(config).Ready, "Other SMTP providers can choose a larger lifetime limit");
        config["MUGONZEI_EMAIL_TOTAL_LIMIT"] = null;
        Check(RegistrationEmailOptions.Load(config).Ready, "An existing generic SMTP configuration remains usable without a lifetime limit");
        config["MUGONZEI_SMTP_HOST"] = "smtpdm.aliyun.com";
        config["MUGONZEI_EMAIL_TOTAL_LIMIT"] = "4";
        var settings = RegistrationEmailOptions.Load(config);
        var service = new RegistrationEmailService(f.Store, settings, mailbox, clock);
        const string address = "member@example.test";
        await service.RequestAsync(address, IPAddress.Loopback, default);
        new AuthService(f.Store).Register(new("budget_member", "test-password-1234", address, mailbox.LastCode), service);
        clock.Advance(TimeSpan.FromMinutes(1));
        await service.RequestAsync(address, IPAddress.Loopback, default);
        Equal("", mailbox.LastCode, "The second reservation sends the already-registered notification");
        mailbox.Fail = true;
        await ThrowsApiAsync(503, () => service.RequestAsync("failure@example.test", IPAddress.Loopback, default));

        clock.Advance(TimeSpan.FromDays(2));
        var resumed = new Store(f.Path);
        var contenders = Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
        {
            var separateMailbox = new TestMailbox();
            var competingService = new RegistrationEmailService(resumed, settings, separateMailbox, clock);
            try { await competingService.RequestAsync($"parallel{i}@example.test", IPAddress.Loopback, default); }
            catch (ApiException ex) when (ex.Code == "email_budget_exhausted") { }
            return separateMailbox.Sends;
        })).ToArray();
        Equal(1, (await Task.WhenAll(contenders)).Sum(), "One concurrent request can take the last lifetime reservation after restart and expiry");
        await ThrowsApiAsync(503, () => service.RequestAsync("blocked@example.test", IPAddress.Loopback, default));
        Equal(3, mailbox.Sends, "Delivery failures and reminders consume reservations, and an exhausted budget never calls SMTP");

        using var daily = new Fixture();
        config["MUGONZEI_EMAIL_TOTAL_LIMIT"] = "1000";
        var mail = new TestMailbox();
        var directMail = new RegistrationEmailService(daily.Store, RegistrationEmailOptions.Load(config), mail, clock);
        for (int i = 0; i < 200; i++)
            await directMail.RequestAsync($"daily{i}@example.test", IPAddress.Parse($"192.0.2.{1 + i / 20}"), default);
        await ThrowsApiAsync(429, () => directMail.RequestAsync("dailycap@example.test", IPAddress.Loopback, default));
        clock.Advance(TimeSpan.FromDays(1));
        await directMail.RequestAsync("nextday@example.test", IPAddress.Loopback, default);
        Equal(201, mail.Sends, "DirectMail clamps an oversized daily setting at 200 and reopens after a rolling day");
    }

    // Exercise the actual SMTP library with a server which advertises AUTH but no STARTTLS.
    private static async Task RefusePlainSmtp()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = new List<string>();
        async Task Observe()
        {
            using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
            await using var stream = peer.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            using var writer = new StreamWriter(stream, Encoding.ASCII, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };
            await writer.WriteLineAsync("220 fixture ESMTP");
            while (await reader.ReadLineAsync(deadline.Token) is { } line)
            {
                received.Add(line);
                if (line.StartsWith("EHLO", StringComparison.OrdinalIgnoreCase)) await writer.WriteLineAsync("250-fixture\r\n250 AUTH LOGIN");
                else if (line.StartsWith("QUIT", StringComparison.OrdinalIgnoreCase)) { await writer.WriteLineAsync("221 Goodbye"); return; }
                else await writer.WriteLineAsync("550 reject");
            }
        }
        var observation = Observe();
        var options = TestEmailOptions(host: "127.0.0.1", port: ((IPEndPoint)listener.LocalEndpoint).Port);
        try
        {
            await new SmtpRegistrationMailer(options).SendAsync("smoke@example.test", "123456", deadline.Token);
            throw new Exception("Unencrypted relay incorrectly accepted");
        }
        catch (SmtpException) { checks++; }
        await observation;
        Check(received.Any(line => line.StartsWith("EHLO", StringComparison.OrdinalIgnoreCase))
            && received.All(line => line.StartsWith("EHLO", StringComparison.OrdinalIgnoreCase) || line.StartsWith("QUIT", StringComparison.OrdinalIgnoreCase)),
            "SMTP without STARTTLS is rejected before AUTH, recipient or code leaves this process");
    }
}
