using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Mugonzei.Server;

internal static partial class Program
{
    private static async Task RedemptionTests()
    {
        RedemptionAccountingAndNormalization();
        RedemptionOptionsAndUnavailability();
        await RedemptionConcurrency();
        RedemptionRollbackAndOverflow();
        RedemptionStorageAndMigration();
        RedemptionAdministrativeCli();
        await RedemptionActualHttpContract();
    }

    private static void RedemptionAccountingAndNormalization()
    {
        using var fixture = new Fixture();
        string user = fixture.User(10), other = fixture.User();
        string job = Guid.NewGuid().ToString();
        fixture.Store.ReserveTranslation(user, job, "synthetic-hash", "{}");
        var generated = fixture.Store.CreateRedemptionCodes(7, 2, DateTimeOffset.UtcNow.AddDays(1), "Synthetic batch");
        Check(generated.Count == 2 && generated.Select(row => row.Code).Distinct().Count() == 2,
            "A batch issues distinct single-use codes");
        Check(generated.All(row => Regex.IsMatch(row.Code, "^MU(?:-[0-9A-F]{4}){8}$", RegexOptions.CultureInvariant)),
            "Generated redemption codes carry eight hexadecimal groups");
        var code = generated[0];
        var redeemed = fixture.Store.RedeemCode(user, " \t" + code.Code.ToLowerInvariant().Replace("-", " -\n") + "\r ");
        Equal(7, redeemed.Credits, "Redemption reports only the server-issued amount");
        Equal(17L, redeemed.Balance, "Redemption adds to the posted balance");
        Equal(1L, redeemed.Reserved, "Redemption preserves an active translation reservation");
        Check(!redeemed.AlreadyRedeemed && !string.IsNullOrWhiteSpace(redeemed.Reference), "First redemption has one stable ledger reference");
        var retry = fixture.Store.RedeemCode(user, code.Code.Replace("-", ""));
        Check(retry.AlreadyRedeemed && retry.Reference == redeemed.Reference, "Normalized same-user retries return the original redemption");
        Equal(17L, retry.Balance, "An ambiguous-response retry never credits twice");
        var ledger = fixture.Store.GetLedger(user).Where(row => row.Reference == redeemed.Reference).ToArray();
        Check(ledger.Length == 1 && ledger[0].Amount == 7 && ledger[0].Balance == 17,
            "Exactly one positive ledger entry records the redemption");
        RedemptionThrows(400, "redemption_unavailable", () => fixture.Store.RedeemCode(other, code.Code));
        Equal(0L, fixture.Store.GetAccount(other).Balance, "A code is single-use across accounts");
        RedemptionThrows(409, "redemption_already_used", () => fixture.Store.RevokeRedemptionCode(code.Id));
        RedemptionSql(fixture.Path, "UPDATE redemption_codes SET expires_at=$expiry WHERE id=$id",
            ("$expiry", DateTimeOffset.UtcNow.AddDays(-1).ToString("O")), ("$id", code.Id));
        Check(new Store(fixture.Path).RedeemCode(user, code.Code).AlreadyRedeemed,
            "An already credited owner's retry survives restart and later expiry");
        fixture.Store.CancelTranslation(user, job);
    }

    private static void RedemptionOptionsAndUnavailability()
    {
        using var fixture = new Fixture(); string user = fixture.User();
        foreach (var invalid in new (int Credits, int Count, DateTimeOffset? ExpiresAt, string Note)[]
        {
            (0, 1, null, ""), (-1, 1, null, ""), (1_000_001, 1, null, ""),
            (1, 0, null, ""), (1, 1001, null, ""),
            (1, 1, DateTimeOffset.UtcNow.AddMinutes(-1), ""),
            (1, 1, DateTimeOffset.UtcNow.AddYears(11), ""),
            (1, 1, null, new string('n', 121)), (1, 1, null, "line\nbreak")
        })
            RedemptionThrows(400, "invalid_redemption_options", () =>
                fixture.Store.CreateRedemptionCodes(invalid.Credits, invalid.Count, invalid.ExpiresAt, invalid.Note));
        Equal(0, fixture.Store.ListRedemptionCodes().Count, "Invalid generation options never persist a partial batch");
        var boundary = fixture.Store.CreateRedemptionCodes(1_000_000, 1, DateTimeOffset.UtcNow.AddYears(9), new string('n', 120)).Single();
        Equal(1_000_000, boundary.Credits, "The maximum documented amount and note length are accepted");
        var expired = fixture.Store.CreateRedemptionCodes(3, 1, null, "expiry fixture").Single();
        RedemptionSql(fixture.Path, "UPDATE redemption_codes SET expires_at=$expiry WHERE id=$id",
            ("$expiry", DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O")), ("$id", expired.Id));
        var revoked = fixture.Store.CreateRedemptionCodes(3, 1, null, "revocation fixture").Single();
        fixture.Store.RevokeRedemptionCode(revoked.Id); fixture.Store.RevokeRedemptionCode(revoked.Id);
        foreach (string code in new[]
        {
            "", " ", "MU-1234", "MU-" + new string('G', 32), "XX-" + new string('A', 32),
            "MU-" + new string('A', 31), "MU-" + new string('A', 33), "MU-" + new string('A', 32),
            "MU-" + new string('\uff21', 32), new string(' ', 81),
            "MU-0000-0000-0000-0000-0000-0000-0000-0000' OR 1=1--", expired.Code, revoked.Code
        })
            RedemptionThrows(400, "redemption_unavailable", () => fixture.Store.RedeemCode(user, code));
        Equal(0L, fixture.Store.GetAccount(user).Balance, "Unavailable and malformed codes cannot alter the balance");
        Equal(0, fixture.Store.GetLedger(user).Count, "Rejected redemptions do not add accounting records");
    }

    private static async Task RedemptionConcurrency()
    {
        using var fixture = new Fixture(); string first = fixture.User(), second = fixture.User();
        var alternate = new Store(fixture.Path);
        for (int round = 0; round < 8; round++)
        {
            var code = fixture.Store.CreateRedemptionCodes(2, 1, null, "concurrent synthetic").Single();
            using var gate = new ManualResetEventSlim();
            Task<bool> Attempt(Store store, string user) => Task.Run(() =>
            {
                gate.Wait();
                try { return !store.RedeemCode(user, code.Code).AlreadyRedeemed; }
                catch (ApiException error) when (error.StatusCode == 400 && error.Code == "redemption_unavailable") { return false; }
            });
            Task<bool> a = Attempt(fixture.Store, first), b = Attempt(alternate, second);
            gate.Set();
            var results = await Task.WhenAll(a, b);
            Equal(1, results.Count(value => value), "Concurrent accounts cannot both consume a single code");
        }
        Equal(16L, fixture.Store.GetAccount(first).Balance + fixture.Store.GetAccount(second).Balance,
            "Concurrent redemption preserves the exact total number of issued credits");
        Equal(8, fixture.Store.GetLedger(first).Count + fixture.Store.GetLedger(second).Count,
            "Each contested code has one ledger record");

        string owner = fixture.User(); var repeated = fixture.Store.CreateRedemptionCodes(9, 1, null, "same account").Single();
        var retries = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => alternate.RedeemCode(owner, repeated.Code))));
        Equal(1, retries.Count(row => !row.AlreadyRedeemed), "Concurrent retries by one account have one first result");
        Equal(1, retries.Select(row => row.Reference).Distinct().Count(), "All concurrent owner retries share the same reference");
        Equal(9L, fixture.Store.GetAccount(owner).Balance, "Concurrent retries credit only once");
        Equal(1, fixture.Store.GetLedger(owner).Count, "Concurrent owner retries have one ledger entry");
    }

    private static void RedemptionRollbackAndOverflow()
    {
        using var fixture = new Fixture(); string user = fixture.User(5), other = fixture.User();
        var code = fixture.Store.CreateRedemptionCodes(4, 1, null, "rollback fixture").Single();
        int ledgerBefore = fixture.Store.GetLedger(user).Count;
        RedemptionSql(fixture.Path, "CREATE TRIGGER fixture_reject_ledger BEFORE INSERT ON ledger BEGIN SELECT RAISE(ABORT,'synthetic ledger failure'); END;");
        try
        {
            try { fixture.Store.RedeemCode(user, code.Code); throw new Exception("Injected ledger failure did not abort redemption"); }
            catch (SqliteException error) when (error.SqliteErrorCode == 19) { checks++; }
        }
        finally { RedemptionSql(fixture.Path, "DROP TRIGGER fixture_reject_ledger;"); }
        Equal(5L, fixture.Store.GetAccount(user).Balance, "A ledger-write failure rolls back the balance update");
        Equal(ledgerBefore, fixture.Store.GetLedger(user).Count, "A failed transaction leaves no partial ledger record");
        Check(!fixture.Store.RedeemCode(other, code.Code).AlreadyRedeemed,
            "A ledger failure also rolls back the code claim so another valid attempt can succeed");

        RedemptionSql(fixture.Path, "UPDATE users SET balance=$balance WHERE id=$user", ("$balance", long.MaxValue - 3), ("$user", user));
        var exact = fixture.Store.CreateRedemptionCodes(3, 1, null, "exact integer boundary").Single();
        Equal(long.MaxValue, fixture.Store.RedeemCode(user, exact.Code).Balance, "The highest exact Int64 balance is supported");
        var overflow = fixture.Store.CreateRedemptionCodes(1, 1, null, "integer overflow").Single();
        int afterExact = fixture.Store.GetLedger(user).Count;
        RedemptionThrows(409, "credit_limit", () => fixture.Store.RedeemCode(user, overflow.Code));
        Equal(long.MaxValue, fixture.Store.GetAccount(user).Balance, "Overflow cannot wrap or convert the balance to a floating-point value");
        Equal(afterExact, fixture.Store.GetLedger(user).Count, "Overflow does not leave a ledger entry");
        Check(!fixture.Store.RedeemCode(other, overflow.Code).AlreadyRedeemed, "An overflow rejection does not burn the code");
    }

    private static void RedemptionStorageAndMigration()
    {
        using var fixture = new Fixture(); string user = fixture.User(5);
        string job = Guid.NewGuid().ToString(); fixture.Store.ReserveTranslation(user, job, "migration-hash", "{}");
        // Reproduce a pre-feature database while retaining the existing users,
        // balances, payment ledger and unfinished translation tables.
        RedemptionSql(fixture.Path, "DROP TABLE redemption_codes;");
        var upgraded = new Store(fixture.Path);
        Equal(5L, upgraded.GetAccount(user).Balance, "Adding redemption support preserves an existing account balance");
        Equal(1L, upgraded.GetAccount(user).Reserved, "Schema initialization does not release unrelated reservations");
        Equal(1, upgraded.GetLedger(user).Count, "Schema initialization preserves the previous payment ledger");
        var code = upgraded.CreateRedemptionCodes(6, 1, null, "public synthetic metadata").Single();
        string canonical = code.Code.Replace("-", "");
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = fixture.Path }.ToString()))
        {
            db.Open(); using var query = db.CreateCommand();
            query.CommandText = "SELECT code_hash FROM redemption_codes WHERE id=$id"; query.Parameters.AddWithValue("$id", code.Id);
            string storedHash = (string)query.ExecuteScalar()!;
            Check(string.Equals(hash, storedHash, StringComparison.OrdinalIgnoreCase), "Persistent lookup stores only the canonical code's SHA-256 hash");
        }
        string listing = JsonSerializer.Serialize(upgraded.ListRedemptionCodes());
        Check(!listing.Contains(code.Code, StringComparison.Ordinal) && !listing.Contains(canonical, StringComparison.Ordinal)
            && !listing.Contains(hash, StringComparison.OrdinalIgnoreCase), "Administrator list metadata omits plaintext codes and lookup hashes");
        foreach (string suffix in new[] { "", "-wal" })
        {
            string file = fixture.Path + suffix;
            if (!File.Exists(file)) continue;
            // SQLite's idle pooled handles remain writable on Windows; permit
            // their existing access while inspecting this quiescent fixture.
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var copy = new MemoryStream(); stream.CopyTo(copy);
            string bytes = Encoding.UTF8.GetString(copy.ToArray());
            Check(!bytes.Contains(code.Code, StringComparison.Ordinal) && !bytes.Contains(canonical, StringComparison.Ordinal),
                "SQLite database and WAL never contain the issued plaintext code");
        }
        var reopened = new Store(fixture.Path);
        Equal(11L, reopened.RedeemCode(user, code.Code).Balance, "A code remains redeemable after store restart");
        Check(new Store(fixture.Path).RedeemCode(user, code.Code).AlreadyRedeemed, "Consumed code state persists across store restarts");
        reopened.CancelTranslation(user, job);
    }

    private static void RedemptionAdministrativeCli()
    {
        using var fixture = new Fixture();
        string outputFile = fixture.Path + ".codes.json", lockedOutput = fixture.Path + ".locked-codes.json";
        string? originalDatabase = Environment.GetEnvironmentVariable("MUGONZEI_DATABASE");
        TextWriter originalOut = Console.Out, originalError = Console.Error;
        var output = new StringWriter(); var errors = new StringWriter();
        try
        {
            Environment.SetEnvironmentVariable("MUGONZEI_DATABASE", fixture.Path);
            Console.SetOut(output); Console.SetError(errors);
            string[] create = ["codes", "create", "--credits", "7", "--count", "2", "--no-expiry", "--note", "CLI synthetic", "--output", outputFile];
            Equal(0, RedemptionCommands.Run(create), "Administrative CLI creates a protected batch export");
            var exported = JsonSerializer.Deserialize<GeneratedRedemptionCode[]>(File.ReadAllText(outputFile), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            Check(exported.Length == 2 && exported.All(row => row.Credits == 7 && row.ExpiresAt is null),
                "Administrative export retains the requested amount and explicit no-expiry option");
            Check(exported.All(row => !output.ToString().Contains(row.Code, StringComparison.Ordinal)),
                "Administrative creation output never prints issued plaintext codes to the console");
            if (!OperatingSystem.IsWindows())
            {
                var mode = File.GetUnixFileMode(outputFile);
                Check((mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) == 0,
                    "Linux code exports are not readable, writable or executable by other users");
            }
            byte[] before = File.ReadAllBytes(outputFile);
            Equal(1, RedemptionCommands.Run(create), "Administrative CLI refuses to overwrite an existing code export");
            Check(before.AsSpan().SequenceEqual(File.ReadAllBytes(outputFile)) && fixture.Store.ListRedemptionCodes().Count == 2,
                "An existing export rejection neither alters the file nor issues another batch");
            output.GetStringBuilder().Clear();
            Equal(0, RedemptionCommands.Run(["codes", "list", "--limit", "10"]), "Administrative CLI lists redacted metadata");
            using (var list = JsonDocument.Parse(output.ToString()))
                Equal(2, list.RootElement.GetArrayLength(), "Administrative list includes the issued batch");
            Check(exported.All(row => !output.ToString().Contains(row.Code, StringComparison.Ordinal)),
                "Administrative metadata cannot recover the exported plaintext codes");
            Equal(0, RedemptionCommands.Run(["codes", "revoke", "--id", exported[0].Id]), "Administrative CLI revokes an unused code");
            string user = fixture.User();
            RedemptionThrows(400, "redemption_unavailable", () => fixture.Store.RedeemCode(user, exported[0].Code));
            Equal(7L, fixture.Store.RedeemCode(user, exported[1].Code).Balance, "An exported code is usable through the normal atomic redemption path");
            using (var lease = new DatabaseLease(fixture.Path))
                Equal(1, RedemptionCommands.Run(["codes", "create", "--credits", "1", "--count", "1", "--output", lockedOutput]),
                    "Administrative CLI refuses a database already leased by the live server");
            Check(!File.Exists(lockedOutput) && fixture.Store.ListRedemptionCodes().Count == 2,
                "Lease rejection performs no export or database mutation");
        }
        finally
        {
            Console.SetOut(originalOut); Console.SetError(originalError);
            Environment.SetEnvironmentVariable("MUGONZEI_DATABASE", originalDatabase);
            foreach (string file in new[] { outputFile, lockedOutput }) if (File.Exists(file)) File.Delete(file);
            output.Dispose(); errors.Dispose();
        }
    }

    private static async Task RedemptionActualHttpContract()
    {
        using var fixture = new Fixture();
        string serverDll = typeof(Store).Assembly.Location;
        string runtimeDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        string dotnet = Path.Combine(Directory.GetParent(runtimeDirectory)!.Parent!.Parent!.FullName,
            OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        Check(File.Exists(dotnet) && File.Exists(Path.ChangeExtension(serverDll, ".runtimeconfig.json")),
            "Actual HTTP regression has the local .NET host and server runtime configuration");
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start(); int port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop();
        var address = new Uri($"http://127.0.0.1:{port}/");
        var start = new ProcessStartInfo(dotnet)
        {
            WorkingDirectory = Path.GetDirectoryName(fixture.Path)!, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(serverDll); start.ArgumentList.Add("--urls"); start.ArgumentList.Add(address.AbsoluteUri);
        // All transports are explicitly unavailable. This child only handles
        // synthetic auth and redemption requests against a throwaway database.
        foreach (string key in start.Environment.Keys.Where(key => key.StartsWith("MUGONZEI_", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("AFDIAN_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("Translation__", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("Kestrel__", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production"; start.Environment["DOTNET_ENVIRONMENT"] = "Production";
        start.Environment["MUGONZEI_DATABASE"] = fixture.Path;
        start.Environment["MUGONZEI_LIVE_PAYMENTS"] = "false"; start.Environment["MUGONZEI_MOCK_PAYMENTS"] = "false";
        start.Environment["MUGONZEI_PLANS_JSON"] = JsonSerializer.Serialize(new[] { new Plan("draft", "Synthetic unpublished plan", 10, 100, "draft-plan") }, TranslationInput.Json);
        start.Environment["Translation__Mock"] = "false"; start.Environment["Translation__BaseUrl"] = "";
        start.Environment["Translation__ApiKey"] = ""; start.Environment["Translation__Model"] = "";
        start.Environment["Logging__LogLevel__Default"] = "Warning";
        using var process = Process.Start(start) ?? throw new Exception("Could not start isolated redemption HTTP server");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        try
        {
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = address };
            bool ready = false;
            for (int attempt = 0; attempt < 100; attempt++)
            {
                if (process.HasExited) throw new Exception("Isolated redemption server exited before health became ready: " + await stderr);
                try { using var health = await http.GetAsync("health", deadline.Token); if (health.IsSuccessStatusCode) { ready = true; break; } }
                catch (HttpRequestException) { }
                await Task.Delay(50, deadline.Token);
            }
            Check(ready, "Actual production entry point starts for the isolated redemption contract test");
            var publicPlans = await http.GetFromJsonAsync<JsonElement[]>("v1/plans", deadline.Token);
            Equal(0, publicPlans!.Length, "Configured draft plans stay hidden while both real and mock payments are disabled");
            var issued = fixture.Store.CreateRedemptionCodes(12, 2, null, "HTTP synthetic");
            using var unauthenticated = await http.PostAsJsonAsync("v1/redemptions", new { code = issued[0].Code }, deadline.Token);
            Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode, "Actual redemption endpoint rejects missing sessions");
            using var noEmail = await http.PostAsJsonAsync("v1/auth/register", new Credentials("obsolete_client", "synthetic-registration-password"), deadline.Token);
            Equal(HttpStatusCode.BadRequest, noEmail.StatusCode, "Legacy registration payload cannot bypass the email challenge");
            using var noMailer = await http.PostAsJsonAsync("v1/auth/register/email-code", new { email = "fixture@example.test" }, deadline.Token);
            Equal(HttpStatusCode.ServiceUnavailable, noMailer.StatusCode, "Unconfigured production sender never pretends to deliver email");
            async Task<AuthReply> LoginLegacy(string name)
            {
                var credentials = new Credentials(name, "synthetic-redemption-password-123");
                fixture.LegacyUser(credentials);
                using var response = await http.PostAsJsonAsync("v1/auth/login", credentials, deadline.Token);
                response.EnsureSuccessStatusCode();
                return (await response.Content.ReadFromJsonAsync<AuthReply>(deadline.Token))!;
            }
            AuthReply first = await LoginLegacy("redeem_http_first"), second = await LoginLegacy("redeem_http_second");
            http.DefaultRequestHeaders.Authorization = new("Bearer", first.Token);
            using var disabledPurchase = await http.PostAsJsonAsync("v1/orders", new { planId = "draft", requestId = Guid.NewGuid() }, deadline.Token);
            Equal(HttpStatusCode.ServiceUnavailable, disabledPurchase.StatusCode, "Hiding draft plans preserves the disabled payment guard on order creation");
            using var response = await http.PostAsJsonAsync("v1/redemptions", new { code = issued[0].Code.ToLowerInvariant() }, deadline.Token);
            Equal(HttpStatusCode.OK, response.StatusCode, "Authenticated HTTP redemption succeeds");
            var result = (await response.Content.ReadFromJsonAsync<RedemptionResult>(deadline.Token))!;
            Check(result.Credits == 12 && result.Balance == 12 && result.Reserved == 0 && !result.AlreadyRedeemed,
                "The actual endpoint returns the desktop amount, balance, reserved and retry contract");
            Check(!string.IsNullOrWhiteSpace(result.Reference), "HTTP redemption exposes its stable ledger reference");
            using var retry = await http.PostAsJsonAsync("v1/redemptions", new { code = issued[0].Code }, deadline.Token);
            Check((await retry.Content.ReadFromJsonAsync<RedemptionResult>(deadline.Token))!.AlreadyRedeemed,
                "HTTP retry confirms the prior credit instead of charging or crediting again");
            HttpStatusCode status = HttpStatusCode.OK;
            for (int attempt = 0; attempt < 12 && status != HttpStatusCode.TooManyRequests; attempt++)
            {
                using var rejected = await http.PostAsJsonAsync("v1/redemptions", new { code = "MU-" + new string('F', 32) }, deadline.Token);
                status = rejected.StatusCode;
                Check(status is HttpStatusCode.BadRequest or HttpStatusCode.TooManyRequests, "Unavailable code requests have a controlled HTTP error");
            }
            Equal(HttpStatusCode.TooManyRequests, status, "Actual redemption route applies its own request limit");
            using var account = await http.GetAsync("v1/account", deadline.Token);
            Equal(HttpStatusCode.OK, account.StatusCode, "Redemption limits leave account recovery queries available");
            using var cancel = await http.PostAsync("v1/translations/" + Guid.NewGuid() + "/cancel", null, deadline.Token);
            Equal(HttpStatusCode.OK, cancel.StatusCode, "Redemption limits do not consume the translation cancellation quota");
            http.DefaultRequestHeaders.Authorization = new("Bearer", second.Token);
            using var independent = await http.PostAsJsonAsync("v1/redemptions", new { code = issued[1].Code }, deadline.Token);
            Equal(HttpStatusCode.OK, independent.StatusCode, "One user's exhausted redemption quota does not block another user on the same IP");
            using var stolen = await http.PostAsJsonAsync("v1/redemptions", new { code = issued[0].Code }, deadline.Token);
            Equal(HttpStatusCode.BadRequest, stolen.StatusCode, "Cross-account redemption replay is rejected through HTTP");
            string denial = await stolen.Content.ReadAsStringAsync(deadline.Token);
            Check(denial.Contains("redemption_unavailable", StringComparison.Ordinal) && !denial.Contains(issued[0].Code, StringComparison.Ordinal)
                && !denial.Contains(first.Username, StringComparison.Ordinal), "HTTP errors disclose neither code nor its owner's identity");
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            await stdout; await stderr;
        }
    }

    private static void RedemptionThrows(int status, string code, Action action)
    {
        try { action(); }
        catch (ApiException error) when (error.StatusCode == status && error.Code == code) { checks++; return; }
        throw new Exception($"Expected redemption API error {status}/{code}");
    }

    private static void RedemptionSql(string path, string sql, params (string Name, object Value)[] parameters)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        db.Open(); using var command = db.CreateCommand(); command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        command.ExecuteNonQuery();
    }
}
