using Microsoft.Data.Sqlite;

namespace Mugonzei.Server;

internal static class AdminAccounts
{
    internal static void Map(RouteGroupBuilder authorized)
    {
        var accounts = authorized.MapGroup("/accounts");
        accounts.MapGet("", (Store store, string? q, int? page, int? pageSize) =>
            store.ListAdminAccounts(q, page ?? 1, pageSize ?? 20));
        accounts.MapGet("/{id}", (Store store, string id, int? orderPage, int? ledgerPage, int? pageSize) =>
            store.GetAdminAccount(id, orderPage ?? 1, ledgerPage ?? 1, pageSize ?? 10));
        accounts.MapGet("/{id}/actions", (Store store, string id, int? page, int? pageSize) =>
            store.GetAdminAccountActions(id, page ?? 1, pageSize ?? 10));
    }
}

internal sealed record AdminAccountRow(string Id, string Username, long Balance, long Reserved, bool Disabled)
{
    public long Available => Balance - Reserved;
}
internal sealed record AdminOrderRow(string Id, string Name, int Credits, int AmountFen, string Status, DateTimeOffset CreatedAt);
internal sealed record AdminLedgerRow(long Id, string Kind, long Amount, long Balance, DateTimeOffset CreatedAt);
internal sealed record AdminActionRow(long Sequence, string Action, long Delta, string Reason, DateTimeOffset CreatedAt, long BalanceAfter);
internal sealed record AdminPage<T>(IReadOnlyList<T> Items, long Total, int Page, int PageSize);
internal sealed record AdminAccountDetail(AdminAccountRow Account, AdminPage<AdminOrderRow> Orders, AdminPage<AdminLedgerRow> Ledger);

public sealed partial class Store
{
    private const string AdminAccountColumns = """
        u.id,u.username,u.balance,u.reserved,
        EXISTS(SELECT 1 FROM account_suspensions s WHERE s.user_id=u.id)
        """;
    private static AdminAccountRow ReadAdminAccountRow(SqliteDataReader r) =>
        new(r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetInt64(3), r.GetBoolean(4));

    internal AdminPage<AdminAccountRow> ListAdminAccounts(string? search, int page, int pageSize)
    {
        CheckAdminPage(page, pageSize);
        if (search is { Length: > 64 } || search?.Any(char.IsControl) == true)
            throw new ApiException(400, "invalid_account_search", "账号查询最多可输入 64 个字符。");

        string text = search?.Trim().ToLowerInvariant() ?? "";
        string from = "FROM users u";
        (string, object?)[] filters = [];
        if (text.Length > 0)
        {
            // Usernames are normalized at registration. instr treats _ and % literally.
            from += " WHERE instr(u.username,$text)>0 OR u.id=$exactId";
            string? exactId = Guid.TryParse(text, out var id) ? id.ToString("N") : null;
            filters = [("$text", text), ("$exactId", exactId)];
        }
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: true);
        var results = ReadAdminPage(db, tx, page, pageSize, from, AdminAccountColumns, "u.username", ReadAdminAccountRow, filters);
        tx.Commit();
        return results;
    }

    internal AdminAccountDetail GetAdminAccount(string id, int orderPage, int ledgerPage, int pageSize)
    {
        CheckAdminPage(orderPage, pageSize);
        CheckAdminPage(ledgerPage, pageSize);
        id = Id(id);
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: true);
        AdminAccountRow account;
        using (var query = Command(db, tx, "SELECT " + AdminAccountColumns + " FROM users u WHERE u.id=$user", ("$user", id)))
        using (var reader = query.ExecuteReader())
            account = reader.Read() ? ReadAdminAccountRow(reader) : throw Missing();
        var orders = ReadAdminPage(db, tx, orderPage, pageSize,
            "FROM orders WHERE user_id=$user", "id,name,credits,amount_fen,status,created_at", "created_at DESC,id DESC",
            r => new AdminOrderRow(r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3), r.GetString(4), Date(r.GetString(5))),
            ("$user", id));
        var ledger = ReadAdminPage(db, tx, ledgerPage, pageSize,
            "FROM ledger WHERE user_id=$user", "id,kind,amount,balance,created_at", "id DESC",
            r => new AdminLedgerRow(r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.GetInt64(3), Date(r.GetString(4))),
            ("$user", id));
        tx.Commit();
        return new(account, orders, ledger);
    }

    internal AdminPage<AdminActionRow> GetAdminAccountActions(string id, int page, int pageSize)
    {
        CheckAdminPage(page, pageSize);
        id = Id(id);
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: true);
        using var exists = Command(db, tx, "SELECT 1 FROM users WHERE id=$user", ("$user", id));
        if (exists.ExecuteScalar() is null) throw Missing();
        var actions = ReadAdminPage(db, tx, page, pageSize,
            "FROM admin_account_actions WHERE user_id=$user", "sequence,action,delta,reason,created_at,balance_after", "sequence DESC",
            r => new AdminActionRow(r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.GetString(3), Date(r.GetString(4)), r.GetInt64(5)),
            ("$user", id));
        tx.Commit();
        return actions;
    }

    private static void CheckAdminPage(int page, int pageSize)
    {
        if (page < 1 || pageSize is < 1 or > 50)
            throw new ApiException(400, "invalid_account_page", "页码从 1 开始，每页可查看 1–50 条。");
    }

    // from, columns and order are internal SQL fragments. All user input is a bound parameter.
    // The caller's read transaction keeps counts, rows and balances on the same snapshot.
    private static AdminPage<T> ReadAdminPage<T>(SqliteConnection db, SqliteTransaction tx, int page, int pageSize,
        string from, string columns, string order, Func<SqliteDataReader, T> read, params (string, object?)[] filters)
    {
        using var count = Command(db, tx, "SELECT COUNT(*) " + from, filters);
        long total = (long)count.ExecuteScalar()!;
        long offset = (long)(page - 1) * pageSize;
        if (offset >= total) return new([], total, page, pageSize);

        using var query = Command(db, tx, $"SELECT {columns} {from} ORDER BY {order} LIMIT $size OFFSET $offset", filters);
        query.Parameters.AddWithValue("$size", pageSize);
        query.Parameters.AddWithValue("$offset", offset);
        using var reader = query.ExecuteReader();
        var items = new List<T>(pageSize);
        while (reader.Read()) items.Add(read(reader));
        return new(items, total, page, pageSize);
    }
}
