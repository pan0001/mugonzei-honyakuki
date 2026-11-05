using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SteamVrTranslator;

/// <summary>One Windows-user-protected session per canonical service URL. No passwords or translations are stored.</summary>
public sealed class CreditSessionStore
{
    private static readonly object Gate = new();
    public string DirectoryPath { get; }

    public CreditSessionStore(string? directoryPath = null) => DirectoryPath = directoryPath ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamVrTranslator", "CreditSessions");

    public CreditSession? Load(string serverUrl)
    {
        string endpoint = CreditServiceClient.NormalizeServerUrl(serverUrl);
        lock (Gate) return LoadCore(endpoint);
    }

    internal void Save(string serverUrl, string token, string username, DateTimeOffset expiresAt)
    {
        string endpoint = CreditServiceClient.NormalizeServerUrl(serverUrl);
        if (string.IsNullOrWhiteSpace(token) || token.Length > 8192 || token.Any(char.IsControl)
            || string.IsNullOrWhiteSpace(username) || username.Length > 100 || username.Any(char.IsControl))
            throw new InvalidOperationException("积分服务返回的登录信息无效。");
        lock (Gate)
        {
            var previous = LoadCore(endpoint);
            SaveCore(endpoint, new StoredSession
            {
                Endpoint = endpoint, Token = token, Username = username, ExpiresAt = expiresAt,
                RecentTaskId = previous?.Username == username ? previous.RecentTaskId : null
            });
        }
    }

    internal void RememberTask(string serverUrl, string expectedToken, Guid taskId)
    {
        string endpoint = CreditServiceClient.NormalizeServerUrl(serverUrl);
        lock (Gate)
        {
            var current = LoadCore(endpoint);
            if (current is null || current.Token != expectedToken)
                throw new InvalidOperationException("积分账户已改变，请重新登录后重试。");
            SaveCore(endpoint, new StoredSession { Endpoint = endpoint, Token = current.Token,
                Username = current.Username, ExpiresAt = current.ExpiresAt, RecentTaskId = taskId });
        }
    }

    internal void Remove(string serverUrl, string? expectedToken = null)
    {
        string endpoint = CreditServiceClient.NormalizeServerUrl(serverUrl);
        lock (Gate)
        {
            if (expectedToken is not null && LoadCore(endpoint)?.Token != expectedToken) return;
            string path = SessionPath(endpoint);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private CreditSession? LoadCore(string endpoint)
    {
        string path = SessionPath(endpoint);
        if (!File.Exists(path)) return null;
        try
        {
            if (new FileInfo(path).Length > 65536) throw new InvalidDataException();
            byte[] encrypted = File.ReadAllBytes(path);
            byte[] clear = ProtectedData.Unprotect(encrypted, Entropy(endpoint), DataProtectionScope.CurrentUser);
            try
            {
                var stored = JsonSerializer.Deserialize<StoredSession>(clear);
                if (stored is null || stored.Version != 1 || stored.Endpoint != endpoint
                    || string.IsNullOrWhiteSpace(stored.Token) || stored.Token.Length > 8192 || stored.Token.Any(char.IsControl)
                    || string.IsNullOrWhiteSpace(stored.Username) || stored.Username.Length > 100 || stored.Username.Any(char.IsControl))
                    throw new InvalidDataException();
                return new CreditSession(stored.Token, stored.Username, stored.ExpiresAt, stored.RecentTaskId);
            }
            finally { CryptographicOperations.ZeroMemory(clear); }
        }
        catch (Exception error) when (error is CryptographicException or JsonException or InvalidDataException)
        {
            throw new InvalidOperationException("积分登录信息无法解密或已损坏，请在账户窗口清除本地登录后重新登录。");
        }
    }

    private void SaveCore(string endpoint, StoredSession stored)
    {
        byte[] clear = JsonSerializer.SerializeToUtf8Bytes(stored);
        byte[] encrypted;
        try { encrypted = ProtectedData.Protect(clear, Entropy(endpoint), DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(clear); }
        Directory.CreateDirectory(DirectoryPath);
        string destination = SessionPath(endpoint);
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, encrypted);
            if (File.Exists(destination)) File.Replace(temporary, destination, null);
            else File.Move(temporary, destination);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private string SessionPath(string endpoint) => Path.Combine(DirectoryPath,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(endpoint))) + ".bin");
    private static byte[] Entropy(string endpoint) => Encoding.UTF8.GetBytes("Mugonzei/CreditSession/v1\n" + endpoint);

    private sealed class StoredSession
    {
        public int Version { get; set; } = 1;
        public string Endpoint { get; set; } = "";
        public string Token { get; set; } = "";
        public string Username { get; set; } = "";
        public DateTimeOffset ExpiresAt { get; set; }
        public Guid? RecentTaskId { get; set; }
    }
}

public sealed class CreditSession
{
    internal string Token { get; }
    public string Username { get; }
    public DateTimeOffset ExpiresAt { get; }
    public Guid? RecentTaskId { get; }
    public bool IsExpired => ExpiresAt <= DateTimeOffset.UtcNow;
    internal CreditSession(string token, string username, DateTimeOffset expiresAt, Guid? recentTaskId)
    { Token = token; Username = username; ExpiresAt = expiresAt; RecentTaskId = recentTaskId; }
}
