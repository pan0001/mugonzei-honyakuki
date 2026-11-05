using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SteamVrTranslator;

internal static class CreditSetupLoginTests
{
    private const string Endpoint = "https://setup.example.test/";
    private const string User = "setup_user";
    private const string Password = "synthetic-setup-password";

    // Called on the wizard suite's STA thread. All HTTP replies and sessions are fixtures.
    internal static void Run()
    {
        LoginAndRetry();
        CancelAndSwitchEndpoint();
        RecoverDamagedSession();
        Console.WriteLine("PASS isolated setup login, offline retry, cancellation, endpoint binding and damaged session recovery");
    }

    private static void LoginAndRetry()
    {
        int requests = 0, signIns = 0, registrations = 0;
        var reply = new TaskCompletionSource<CreditSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var login = new CreditSetupLoginControl(Endpoint, (url, user, secret, _) =>
        {
            Check(url == Endpoint && user == User && secret == Password, "credentials sent to selected service");
            return ++requests switch
            {
                1 => Task.FromException<CreditSession>(new CreditServiceException("private diagnostic", isTransient: true)),
                2 => Task.FromException<CreditSession>(new CreditServiceException("private diagnostic", HttpStatusCode.Unauthorized)),
                _ => reply.Task
            };
        });
        using var host = Mount(login);
        var user = Find<TextBox>(login, "wizardLoginUsername");
        var password = Find<TextBox>(login, "wizardLoginPassword");
        var status = Find<Label>(login, "wizardLoginStatus");
        var register = Find<LinkLabel>(login, "wizardLoginRegister");
        login.SignedIn += (_, _) => signIns++;
        login.RegisterRequested += (_, _) => registrations++;
        Check(requests == 0 && !login.IsAuthenticated && password.UseSystemPasswordChar, "opening is offline and password is masked");
        foreach (var input in new[] { user, password })
            Check(input.BorderStyle == BorderStyle.None && input.Parent is UiCard { Height: 42, Width: >= 180 and <= 440 },
                "login field uses the dark padded input frame");
        Check(host.AcceptButton == login.LoginButton && user.TabIndex < password.TabIndex, "login button handles Enter and username comes first");
        login.RegistrationAvailable = false;
        Check(!register.Visible, "custom service can hide official registration");
        login.RegistrationAvailable = true;
        ((IButtonControl)register).PerformClick();
        Check(registrations == 1 && requests == 0 && !login.IsAuthenticated, "registration link never signs in");

        user.Text = "ab"; password.Text = Password;
        Check(!Finish(login.TryLoginAsync()) && requests == 0, "malformed username rejected locally");
        user.Text = User.ToUpperInvariant();
        Check(!Finish(login.TryLoginAsync()) && status.Text.Contains("网络") && !status.Text.Contains("private"), "offline state shows a safe retry message");
        Check(!Finish(login.TryLoginAsync()) && status.Text.Contains("账号或密码") && login.LoginButton.Enabled, "bad credentials can be retried");
        var success = login.TryLoginAsync();
        Check(login.IsBusy && !login.IsAuthenticated && !register.Enabled && !login.LoginButton.Enabled &&
            !Finish(login.TryLoginAsync()) && requests == 3, "pending login cannot advance or double-submit");
        reply.SetResult(Session());
        Check(Finish(success) && !login.IsBusy && login.IsAuthenticated && login.AuthenticatedUsername == User &&
            signIns == 1 && password.Text.Length == 0, "success unlocks exactly once and clears the password");
        login.SetServer("HTTPS://SETUP.EXAMPLE.TEST:443");
        Check(login.IsAuthenticated, "canonical-equivalent endpoint keeps the confirmed login");
        login.SetServer("https://other.example.test/");
        Check(!login.IsAuthenticated && login.AuthenticatedUsername is null && user.Text == "", "different endpoint requires fresh credentials");
    }

    private static void CancelAndSwitchEndpoint()
    {
        foreach (string end in new[] { "back", "switch", "dispose" })
        {
            var delayed = new TaskCompletionSource<CreditSession>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken token = default;
            int requests = 0, completed = 0;
            using var login = new CreditSetupLoginControl(Endpoint, (_, _, _, attempt) =>
            {
                token = attempt;
                return ++requests == 1 ? delayed.Task : Task.FromResult(Session());
            });
            using var host = Mount(login);
            login.SignedIn += (_, _) => completed++;
            Find<TextBox>(login, "wizardLoginUsername").Text = User;
            Find<TextBox>(login, "wizardLoginPassword").Text = Password;
            var first = login.TryLoginAsync();
            if (end == "back") login.CancelPending();
            if (end == "switch") login.SetServer("https://other.example.test/");
            if (end == "dispose") login.Dispose();
            Check(token.IsCancellationRequested && !login.IsBusy && !login.IsAuthenticated, "abandoned attempt is cancelled");
            if (end != "dispose")
            {
                Check(Find<TextBox>(login, "wizardLoginPassword").Text == "", "abandoning login clears its password");
                Find<TextBox>(login, "wizardLoginUsername").Text = User;
                Find<TextBox>(login, "wizardLoginPassword").Text = Password;
                Check(Finish(login.TryLoginAsync()), "new attempt succeeds while abandoned handler unwinds");
            }
            delayed.SetResult(Session());
            Check(!Finish(first) && completed == (end == "dispose" ? 0 : 1), "late response cannot advance the wizard");
        }
    }

    private static void RecoverDamagedSession()
    {
        string directory = Directory.CreateTempSubdirectory("MugonzeiSetupLogin-").FullName;
        try
        {
            var store = new CreditSessionStore(directory);
            store.Save(Endpoint, "old-token", User, DateTimeOffset.UtcNow.AddDays(-1));
            string path = Directory.GetFiles(directory).Single();
            var handler = new LoginReplies();
            using var client = new CreditServiceClient(Endpoint, store, handler);
            Throws<OperationCanceledException>(() => store.Save(Endpoint, "discarded-token", User, DateTimeOffset.UtcNow.AddDays(1), new CancellationToken(true)));
            Check(store.Load(Endpoint)?.Token == "old-token", "cancelled authentication never installs a new session");
            var badJson = ProtectedData.Protect("{"u8.ToArray(), Encoding.UTF8.GetBytes("Mugonzei/CreditSession/v1\n" + Endpoint), DataProtectionScope.CurrentUser);
            foreach (byte[] damaged in new[] { new byte[] { 1, 2, 3 }, badJson })
            {
                File.WriteAllBytes(path, damaged);
                Throws<InvalidOperationException>(() => store.Load(Endpoint));
                handler.Code = HttpStatusCode.Unauthorized;
                Throws<CreditServiceException>(() => client.LoginAsync(User, Password).GetAwaiter().GetResult());
                Check(damaged.AsSpan().SequenceEqual(File.ReadAllBytes(path)), "failed sign-in preserves old session");
                handler.Code = HttpStatusCode.OK;
                handler.Username = "unexpected_user";
                Throws<CreditServiceException>(() => client.LoginAsync(User, Password).GetAwaiter().GetResult());
                Check(damaged.AsSpan().SequenceEqual(File.ReadAllBytes(path)), "mismatched identity cannot replace old credentials");
                handler.Username = User;
                var session = client.LoginAsync(User, Password).GetAwaiter().GetResult();
                Check(session.Username == User && !session.IsExpired && store.Load(Endpoint)?.Token == "new-token",
                    "only authenticated sign-in repairs the stored session");
            }
            using var locked = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);
            Throws<IOException>(() => client.LoginAsync(User, Password).GetAwaiter().GetResult());
        }
        finally
        {
            foreach (string path in Directory.GetFiles(directory)) File.Delete(path);
            Directory.Delete(directory);
        }
    }

    private sealed class LoginReplies : HttpMessageHandler
    {
        internal HttpStatusCode Code = HttpStatusCode.OK;
        internal string Username = User;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Check(request.Method == HttpMethod.Post && request.RequestUri?.AbsoluteUri == Endpoint + "v1/auth/login" &&
                request.Headers.Authorization is null, "login only sends credentials to the selected service's login endpoint");
            return Task.FromResult(new HttpResponseMessage(Code) { Content = new StringContent(JsonSerializer.Serialize(new
            { username = Username, token = "new-token", expiresAt = DateTimeOffset.UtcNow.AddHours(1) })) });
        }
    }

    private static CreditSession Session() => new("test-token", User, DateTimeOffset.UtcNow.AddHours(1), null);
    private static T Find<T>(Control owner, string name) where T : Control => (T)owner.Controls.Find(name, true).Single();
    private static Form Mount(CreditSetupLoginControl login)
    {
        var host = new Form { ShowInTaskbar = false, Opacity = 0, StartPosition = FormStartPosition.Manual,
            Location = new Point(-30000, -30000), ClientSize = new Size(500, 380), AcceptButton = login.LoginButton };
        login.Dock = DockStyle.Top;
        host.Controls.Add(login);
        host.Show();
        return host;
    }
    private static bool Finish(Task<bool> operation)
    {
        var deadline = Stopwatch.StartNew();
        while (!operation.IsCompleted && deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            Application.DoEvents();
            Thread.Yield();
        }
        Check(operation.IsCompleted, "UI continuation completed");
        return operation.GetAwaiter().GetResult();
    }
    private static void Throws<T>(Action work) where T : Exception
    {
        try { work(); } catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException("Setup login test failed: " + message);
    }
}
