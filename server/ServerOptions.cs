using System.Globalization;
using System.Text.Json;

namespace Mugonzei.Server;

public sealed record Plan(string Id, string Name, int Credits, int AmountFen, string AfdianPlanId);
public sealed class ApiException(int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}

public sealed class ServerOptions
{
    public string DatabasePath { get; init; } = "data/mugonzei.db";
    public bool LivePayments { get; init; }
    public bool MockPayments { get; init; }
    public string CreatorId { get; init; } = "";
    public string AfdianToken { get; init; } = "";
    public string PaymentBaseUrl { get; init; } = "";
    public IReadOnlyList<Plan> Plans { get; init; } = [];

    public static ServerOptions Load(IConfiguration configuration, IHostEnvironment environment)
    {
        string Value(string name, string fallback = "") => configuration[name] ?? fallback;
        bool Flag(string name) => bool.TryParse(Value(name), out bool value) && value;
        var options = new ServerOptions
        {
            DatabasePath = Value("MUGONZEI_DATABASE", "data/mugonzei.db"),
            LivePayments = Flag("MUGONZEI_LIVE_PAYMENTS"),
            MockPayments = Flag("MUGONZEI_MOCK_PAYMENTS"),
            CreatorId = Value("AFDIAN_USER_ID"),
            AfdianToken = Value("AFDIAN_TOKEN"),
            PaymentBaseUrl = Value("AFDIAN_PAYMENT_BASE_URL"),
            Plans = ReadPlans(Value("MUGONZEI_PLANS_JSON", "[]"))
        };
        if (options.MockPayments && (!environment.IsDevelopment() || options.LivePayments))
            throw new InvalidOperationException("模拟支付仅允许在 Development 且真实支付关闭时启用。");
        if (options.LivePayments)
        {
            if (string.IsNullOrWhiteSpace(options.CreatorId) || string.IsNullOrWhiteSpace(options.AfdianToken)
                || options.Plans.Count == 0 || options.Plans.Any(p => string.IsNullOrWhiteSpace(p.AfdianPlanId)))
                throw new InvalidOperationException("真实支付需配置创作者、服务器 Token 和爱发电计划映射。");
            ValidatePaymentUrl(options.PaymentBaseUrl);
        }
        return options;
    }

    internal static IReadOnlyList<Plan> ReadPlans(string json)
    {
        var plans = JsonSerializer.Deserialize<List<Plan>>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
        if (plans.Count > 30 || plans.Any(p => p is null || string.IsNullOrWhiteSpace(p.Id) || p.Id.Length > 64 || p.Id.Any(char.IsControl)
                || string.IsNullOrWhiteSpace(p.Name) || p.Name.Length > 100 || p.Credits is < 1 or > 1_000_000
                || p.AmountFen is < 1 or > 10_000_000 || p.AfdianPlanId is null || p.AfdianPlanId.Length > 128)
            || plans.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != plans.Count)
            throw new InvalidOperationException("充值计划配置无效。");
        return plans;
    }

    public static Uri ValidatePaymentUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || uri.Host is not ("afdian.com" or "ifdian.net" or "afdian.net") || uri.AbsolutePath != "/order/create")
            throw new InvalidOperationException("付款地址必须是受支持爱发电域名的 HTTPS /order/create 地址。");
        return uri;
    }

    public Plan GetPlan(string id) => Plans.FirstOrDefault(p => p.Id == id)
        ?? throw new ApiException(400, "unknown_plan", "充值计划不存在。");

    public string BuildPayUrl(OrderRecord order)
    {
        if (MockPayments) return "/dev/payments/" + order.Id;
        var uri = ValidatePaymentUrl(PaymentBaseUrl);
        string E(string value) => Uri.EscapeDataString(value);
        return uri.AbsoluteUri + "?user_id=" + E(CreatorId) + "&plan_id=" + E(order.AfdianPlanId)
            + "&custom_order_id=" + E(order.CustomId)
            + (order.DiscountFen > 0 ? "" : "&custom_price=" + (order.AmountFen / 100m).ToString("0.00", CultureInfo.InvariantCulture));
    }
}
