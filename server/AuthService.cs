using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Mugonzei.Server;

public sealed record AuthReply(string Token, string Username, DateTimeOffset ExpiresAt);
public sealed record Credentials(string Username, string Password);
public sealed class AuthService(Store store)
{
    private const int Iterations = 210_000;
    private static readonly byte[] DummySalt = new byte[16];
    private static readonly byte[] DummyHash = new byte[32];

    public AuthReply Register(Credentials input)
    {
        string username = Normalize(input.Username);
        ValidatePassword(input.Password);
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] hash = HashPassword(input.Password, salt, Iterations);
        string userId = store.RegisterUser(username, hash, salt, Iterations);
        return CreateSession(userId, username);
    }

    public AuthReply Login(Credentials input)
    {
        string username = Normalize(input.Username);
        ValidatePassword(input.Password);
        var user = store.FindUser(username);
        byte[] candidate = HashPassword(input.Password, user?.Salt ?? DummySalt, user?.Iterations ?? Iterations);
        bool equal = CryptographicOperations.FixedTimeEquals(candidate, user?.PasswordHash ?? DummyHash);
        CryptographicOperations.ZeroMemory(candidate);
        if (!equal || user is null) throw new ApiException(401, "invalid_login", "用户名或密码错误。");
        return CreateSession(user.Id, user.Username);
    }

    private AuthReply CreateSession(string userId, string username)
    {
        string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var expires = DateTimeOffset.UtcNow.AddDays(14);
        store.CreateSession(userId, TokenHash(token), expires);
        return new(token, username, expires);
    }

    public void Authenticate(HttpContext context)
    {
        string header = context.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return;
        string token = header[7..];
        if (token.Length != 43 || token.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))) return;
        string hash = TokenHash(token);
        string? userId = store.FindSession(hash, DateTimeOffset.UtcNow);
        if (userId is not null) { context.Items["userId"] = userId; context.Items["sessionHash"] = hash; }
    }

    public void Logout(HttpContext context)
    {
        _ = UserId(context);
        store.RevokeSession((string)context.Items["sessionHash"]!);
    }

    public static string UserId(HttpContext context) => context.Items["userId"] as string
        ?? throw new ApiException(401, "authentication_required", "请先登录，或重新登录已过期的账户。");

    private static string Normalize(string? value)
    {
        string username = value?.Trim().ToLowerInvariant() ?? "";
        if (!Regex.IsMatch(username, "^[a-z0-9_-]{3,32}$", RegexOptions.CultureInvariant))
            throw new ApiException(400, "invalid_username", "用户名需为 3–32 位英文字母、数字、下划线或短横线。");
        return username;
    }
    private static void ValidatePassword(string? password)
    {
        if (password is null || password.Length is < 10 or > 128 || password.Any(char.IsControl))
            throw new ApiException(400, "invalid_password", "密码需为 10–128 个字符，不能包含控制字符。");
    }
    private static byte[] HashPassword(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);
    private static string TokenHash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
