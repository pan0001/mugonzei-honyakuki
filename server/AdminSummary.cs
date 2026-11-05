namespace Mugonzei.Server;

internal static class AdminSummary
{
    internal static void Map(RouteGroupBuilder authorized) =>
        authorized.MapGet("/summary", (Store store) => store.OperatorSummary());
}

internal sealed record OperatorCounts(long Accounts, long PaidOrders, long PaidAmountFen);

public sealed partial class Store
{
    internal OperatorCounts OperatorSummary()
    {
        using var db = Open();
        using var cmd = Command(db, null, """
            SELECT (SELECT COUNT(*) FROM users), COUNT(*), COALESCE(SUM(amount_fen), 0)
            FROM orders WHERE status='paid'
            """);
        using var result = cmd.ExecuteReader();
        result.Read();
        return new(result.GetInt64(0), result.GetInt64(1), result.GetInt64(2));
    }
}
