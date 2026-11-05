using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Mugonzei.Server;

internal sealed class AdminAccess
{
    private const string CookieName = "Mugonzei.Admin";
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);
    private readonly byte[] tokenHash;
    private readonly bool development;
    private readonly string? productionOrigin;
    private readonly TimeProvider clock;
    private readonly Dictionary<string, DateTimeOffset> sessions = new(StringComparer.Ordinal);

    private AdminAccess(string token, bool development, string? productionOrigin, TimeProvider clock)
    {
        tokenHash = SHA256.HashData(Encoding.ASCII.GetBytes(token));
        this.development = development;
        this.productionOrigin = productionOrigin;
        this.clock = clock;
    }

    internal static AdminAccess? Load(IConfiguration config, IHostEnvironment environment, TimeProvider clock)
    {
        string? token = config["MUGONZEI_ADMIN_TOKEN"];
        if (token is not { Length: >= 32 and <= 256 } || token.Any(c => c is < '!' or > '~') ||
            token.Distinct().Take(8).Count() < 8) return null;
        bool development = environment.IsDevelopment();
        string? origin = HostOrigin("https", config["MUGONZEI_ADMIN_HOST"]);
        if (!development && origin is null) return null;
        return new(token, development, origin, clock);
    }

    private static string? HostOrigin(string scheme, string? host)
    {
        // Accept an authority only. Uri also accepts credentials, paths and query strings, which are never valid here.
        if (host is not { Length: > 0 and <= 260 } ||
            host.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or ':' or '[' or ']')) ||
            !Uri.TryCreate(scheme + "://" + host, UriKind.Absolute, out var uri) ||
            uri.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6) ||
            uri.Port < 1 || host.EndsWith(':')) return null;
        return uri.GetLeftPart(UriPartial.Authority);
    }

    internal bool AllowsOriginHost(HttpContext context)
    {
        var request = context.Request;
        if (!development)
            return request.IsHttps && string.Equals(HostOrigin("https", request.Host.Value), productionOrigin, StringComparison.OrdinalIgnoreCase);
        var peer = context.Connection.RemoteIpAddress;
        if (peer is null || !IPAddress.IsLoopback(peer.IsIPv4MappedToIPv6 ? peer.MapToIPv4() : peer) ||
            request.Scheme is not ("http" or "https") ||
            HostOrigin(request.Scheme, request.Host.Value) is null ||
            request.Host.Host.ToLowerInvariant() is not ("localhost" or "127.0.0.1" or "[::1]" or "::1")) return false;
        // ForwardedHeaders may already have consumed these headers and stored the original values.
        return !request.Headers.Keys.Any(name => name.Equals("Forwarded", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("X-Original-", StringComparison.OrdinalIgnoreCase));
    }

    internal static bool IsSameOriginWrite(HttpRequest request)
    {
        var marker = request.Headers["X-Mugonzei-Admin"];
        var origin = request.Headers.Origin;
        return marker.Count == 1 && marker[0] == "1" && origin.Count == 1 &&
            string.Equals(origin[0], HostOrigin(request.Scheme, request.Host.Value), StringComparison.OrdinalIgnoreCase);
    }

    private static string? SessionHash(HttpRequest request)
    {
        string? value = request.Cookies[CookieName];
        return value is { Length: 64 } && value.All(char.IsAsciiHexDigit)
            ? Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(value))) : null;
    }

    private static CookieOptions CookieSettings(HttpRequest request) => new()
    {
        Path = "/admin", Secure = request.IsHttps, HttpOnly = true, SameSite = SameSiteMode.Strict, IsEssential = true
    };

    internal DateTimeOffset Login(HttpContext context, string? supplied)
    {
        if (supplied is not { Length: > 0 and <= 256 } ||
            !CryptographicOperations.FixedTimeEquals(tokenHash, SHA256.HashData(Encoding.UTF8.GetBytes(supplied))))
            throw new ApiException(401, "admin_unauthorized", "开发者登录凭据无效。");
        string secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        string key = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(secret)));
        string? previous = SessionHash(context.Request);
        var now = clock.GetUtcNow();
        var expires = now + Lifetime;
        lock (sessions)
        {
            if (previous is not null) sessions.Remove(previous);
            foreach (var expired in sessions.Where(pair => pair.Value <= now).Select(pair => pair.Key).ToArray()) sessions.Remove(expired);
            if (sessions.Count >= 64) sessions.Remove(sessions.MinBy(pair => pair.Value).Key);
            sessions.Add(key, expires);
        }
        var cookie = CookieSettings(context.Request);
        cookie.Expires = expires;
        cookie.MaxAge = Lifetime;
        context.Response.Cookies.Append(CookieName, secret, cookie);
        return expires;
    }

    internal DateTimeOffset? SessionExpiry(HttpRequest request)
    {
        string? key = SessionHash(request);
        if (key is null) return null;
        lock (sessions)
        {
            if (!sessions.TryGetValue(key, out var expires)) return null;
            if (expires > clock.GetUtcNow()) return expires;
            sessions.Remove(key);
            return null;
        }
    }

    internal void Logout(HttpContext context)
    {
        string? key = SessionHash(context.Request);
        if (key is not null) { lock (sessions) sessions.Remove(key); }
        context.Response.Cookies.Delete(CookieName, CookieSettings(context.Request));
    }
}
