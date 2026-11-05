using System.Globalization;
using System.Text.Json;

namespace Mugonzei.Server;

/// <summary>Local administrative commands only; no public code-generation endpoint.</summary>
public static class RedemptionCommands
{
    private const string Usage = "用法：codes create --credits N --count N --output 受保护的绝对路径.json [--expires UTC时间 | --no-expiry] [--note 备注]；codes list [--limit 100]；codes revoke --id UUID。运行前先停止后台服务，完成后重新启动。";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static int Run(string[] args)
    {
        try
        {
            if (args.Length == 2 && args[1] == "--help") { Console.WriteLine(Usage); return 0; }
            if (args.Length < 2 || args[1] is not ("create" or "list" or "revoke")) throw new ArgumentException(Usage);
            string mode = args[1];
            string[] allowed = mode switch
            {
                "create" => ["--credits", "--count", "--output", "--expires", "--no-expiry", "--note"],
                "list" => ["--limit"],
                _ => ["--id"]
            };
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 2; i < args.Length; i++)
            {
                string key = args[i];
                if (!allowed.Contains(key) || values.ContainsKey(key)) throw new ArgumentException(Usage);
                if (key == "--no-expiry") values.Add(key, "true");
                else
                {
                    if (++i >= args.Length || args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException(Usage);
                    values.Add(key, args[i]);
                }
            }
            string Required(string key) => values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value : throw new ArgumentException(Usage);
            int Number(string key, int? defaultValue = null) => !values.ContainsKey(key) && defaultValue.HasValue
                ? defaultValue.Value : int.TryParse(Required(key), NumberStyles.None, CultureInfo.InvariantCulture, out int result)
                ? result : throw new ArgumentException(Usage);

            string database = Environment.GetEnvironmentVariable("MUGONZEI_DATABASE") ?? "data/mugonzei.db";
            if (!File.Exists(database)) throw new InvalidOperationException("指定数据库不存在；管理命令不会创建新数据库。");
            using var lease = new DatabaseLease(database);
            var store = new Store(database);
            if (mode == "list")
            {
                Console.WriteLine(JsonSerializer.Serialize(store.ListRedemptionCodes(Number("--limit", 100)), Json));
                return 0;
            }
            if (mode == "revoke")
            {
                Console.WriteLine(JsonSerializer.Serialize(store.RevokeRedemptionCode(Required("--id")), Json));
                return 0;
            }

            int credits = Number("--credits"), count = Number("--count");
            if (values.ContainsKey("--expires") && values.ContainsKey("--no-expiry")) throw new ArgumentException(Usage);
            DateTimeOffset? expires = DateTimeOffset.UtcNow.AddDays(30);
            if (values.ContainsKey("--no-expiry")) expires = null;
            else if (values.TryGetValue("--expires", out var date))
            {
                if (!DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
                    throw new ArgumentException("有效期格式不正确，请使用带时区的 ISO 8601 时间。");
                expires = parsed.ToUniversalTime();
            }
            string output = Required("--output");
            if (!Path.IsPathFullyQualified(output) || !output.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("输出必须是新的 JSON 文件绝对路径；请使用网站目录及 Git 仓库以外的受保护目录。");
            output = Path.GetFullPath(output);
            var fileOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.WriteThrough };
            if (!OperatingSystem.IsWindows()) fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            IReadOnlyList<GeneratedRedemptionCode>? generated = null;
            // CreateNew prevents replacing an existing export or following an existing file symlink.
            using (var file = new FileStream(output, fileOptions))
            {
                try
                {
                    generated = store.CreateRedemptionCodes(credits, count, expires, values.GetValueOrDefault("--note", ""));
                    JsonSerializer.Serialize(file, generated, Json);
                    file.Flush(flushToDisk: true);
                }
                catch
                {
                    // If the export is incomplete, none of its codes should be distributed as usable.
                    if (generated is not null)
                        foreach (var code in generated) store.RevokeRedemptionCode(code.Id);
                    throw;
                }
            }
            Console.WriteLine(JsonSerializer.Serialize(new { created = generated.Count, output, expiresAt = expires,
                message = "明文兑换码只写入受保护的导出文件，请妥善保存；列表不会再次显示兑换码。" }, Json));
            return 0;
        }
        catch (Exception error) when (error is ApiException or ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            // Never include supplied codes, command lines or serialized batches in errors.
            Console.Error.WriteLine(error is IOException or UnauthorizedAccessException
                ? "文件访问失败；不会覆盖现有导出。若生成后导出失败，已生成的码会撤销，请检查元数据后使用新文件重试。"
                : error.Message);
            return 1;
        }
    }
}
