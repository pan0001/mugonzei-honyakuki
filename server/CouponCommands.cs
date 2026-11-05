using System.Globalization;
using System.Text.Json;

namespace Mugonzei.Server;

/// <summary>Offline, local operator commands. No public endpoint issues or changes promotions.</summary>
public static class CouponCommands
{
    private const string Usage = """
        coupons create --plan 套餐ID --amount-fen 折后价(分) --afdian-plan 折扣方案ID [--days 30] [--code-stdin]
        coupons list
        coupons revoke --id 活动ID
        create 默认生成随机优惠码；--code-stdin 从标准输入读取一行自定义口令。成功时只输出一次明文，勿打入共享日志。
        使用同一 MUGONZEI_DATABASE、MUGONZEI_PLANS_JSON，先停止占用此数据库的后台，再运行管理命令。
        """;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static int Run(string[] args)
    {
        if (args.Length == 2 && args[1] == "--help") { Console.WriteLine(Usage); return 0; }
        try
        {
            if (args.Length < 2 || args[1] is not ("create" or "list" or "revoke")) throw new ArgumentException(Usage);
            string mode = args[1];
            var opts = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 2; i < args.Length; i++)
            {
                string key = args[i];
                bool accepted = mode == "create" && key is ("--plan" or "--amount-fen" or "--afdian-plan" or "--days" or "--code-stdin")
                    || mode == "revoke" && key == "--id";
                if (!accepted || opts.ContainsKey(key)) throw new ArgumentException(Usage);
                if (key == "--code-stdin") opts.Add(key, "true");
                else if (++i < args.Length && !string.IsNullOrWhiteSpace(args[i]) && !args[i].StartsWith("--"))
                    opts.Add(key, args[i]);
                else throw new ArgumentException(Usage);
            }
            string Required(string key) => opts.GetValueOrDefault(key) ?? throw new ArgumentException(Usage);
            int Parse(string input) => int.TryParse(input, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : throw new ArgumentException(Usage);
            string db = Environment.GetEnvironmentVariable("MUGONZEI_DATABASE") ?? "data/mugonzei.db";
            if (!File.Exists(db)) throw new InvalidOperationException("指定数据库不存在；管理命令不会创建新数据库。");
            using var lease = new DatabaseLease(db);
            var store = new Store(db);
            if (mode == "list") Console.WriteLine(JsonSerializer.Serialize(store.ListCoupons(), Json));
            else if (mode == "revoke")
            {
                string id = Required("--id");
                Console.WriteLine(JsonSerializer.Serialize(new { id, revoked = store.RevokeCoupon(id) }, Json));
            }
            else
            {
                var plans = ServerOptions.ReadPlans(Environment.GetEnvironmentVariable("MUGONZEI_PLANS_JSON") ?? "[]");
                int days = Parse(opts.GetValueOrDefault("--days") ?? "30");
                if (days is < 1 or > 365) throw new ArgumentException("活动有效期为 1 至 365 天。");
                string? code = opts.ContainsKey("--code-stdin") ? Console.ReadLine() : null;
                if (opts.ContainsKey("--code-stdin") && string.IsNullOrWhiteSpace(code)) throw new ArgumentException("自定义优惠码不能为空。");
                var issued = store.CreateCoupon(plans, Required("--plan"), Parse(Required("--amount-fen")),
                    Required("--afdian-plan"), DateTimeOffset.UtcNow.AddDays(days), code);
                Console.WriteLine(JsonSerializer.Serialize(issued, Json));
            }
            return 0;
        }
        catch (Exception e) when (e is ApiException or ArgumentException or InvalidOperationException or IOException or
            UnauthorizedAccessException or JsonException or Microsoft.Data.Sqlite.SqliteException)
        {
            Console.Error.WriteLine(e is ApiException or ArgumentException or InvalidOperationException ? e.Message : "无法读取套餐配置或操作优惠码数据库。");
            return 1;
        }
    }
}
