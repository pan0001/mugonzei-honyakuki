using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace Mugonzei.Server;

public sealed record UserRecord(string Id, string Username, byte[] PasswordHash, byte[] Salt, int Iterations);
public sealed record AccountRecord(string Username, long Balance, long Reserved);
public sealed record LedgerRecord(string Kind, long Amount, long Balance, DateTimeOffset CreatedAt, string Reference);
public sealed record OrderRecord(string Id, string UserId, string RequestId, string PlanId, string Name,
    int Credits, int AmountFen, string AfdianPlanId, string CustomId, string Status, DateTimeOffset CreatedAt);
public sealed record RefundRecord(string Id, string OrderId, string Status, int Credits, DateTimeOffset CreatedAt);

/// <summary>All monetary state transitions occur in immediate SQLite transactions.</summary>
public sealed class Store
{
    private readonly string connectionString;
    public Store(string databasePath)
    {
        string path = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        connectionString = new SqliteConnectionStringBuilder { DataSource = path, DefaultTimeout = 15 }.ToString();
        using var db = Open();
        Execute(db, null, "PRAGMA journal_mode=WAL;");
        Execute(db, null, """
            CREATE TABLE IF NOT EXISTS users (
              id TEXT PRIMARY KEY, username TEXT NOT NULL UNIQUE, password_hash BLOB NOT NULL,
              salt BLOB NOT NULL, iterations INTEGER NOT NULL,
              balance INTEGER NOT NULL DEFAULT 0 CHECK(balance >= 0),
              reserved INTEGER NOT NULL DEFAULT 0 CHECK(reserved >= 0 AND reserved <= balance));
            CREATE TABLE IF NOT EXISTS sessions (
              token_hash TEXT PRIMARY KEY, user_id TEXT NOT NULL REFERENCES users(id), expires_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS orders (
              id TEXT PRIMARY KEY, user_id TEXT NOT NULL REFERENCES users(id), request_id TEXT NOT NULL,
              plan_id TEXT NOT NULL, name TEXT NOT NULL, credits INTEGER NOT NULL, amount_fen INTEGER NOT NULL,
              afdian_plan_id TEXT NOT NULL, custom_id TEXT NOT NULL UNIQUE, status TEXT NOT NULL,
              created_at TEXT NOT NULL, provider_trade_no TEXT UNIQUE, UNIQUE(user_id, request_id));
            CREATE TABLE IF NOT EXISTS ledger (
              id INTEGER PRIMARY KEY AUTOINCREMENT, user_id TEXT NOT NULL REFERENCES users(id),
              kind TEXT NOT NULL, amount INTEGER NOT NULL, balance INTEGER NOT NULL,
              created_at TEXT NOT NULL, reference TEXT NOT NULL, UNIQUE(user_id, reference));
            CREATE TABLE IF NOT EXISTS refunds (
              id TEXT PRIMARY KEY, order_id TEXT NOT NULL REFERENCES orders(id), status TEXT NOT NULL, created_at TEXT NOT NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS refund_active ON refunds(order_id) WHERE status IN ('pending','confirmed');
            CREATE TABLE IF NOT EXISTS translations (
              id TEXT NOT NULL, user_id TEXT NOT NULL REFERENCES users(id), body_hash TEXT NOT NULL,
              input_json TEXT NOT NULL, status TEXT NOT NULL, result_json TEXT, error_code TEXT,
              created_at TEXT NOT NULL, updated_at TEXT NOT NULL, PRIMARY KEY(user_id,id));
            CREATE INDEX IF NOT EXISTS ledger_user_created ON ledger(user_id,id);
            CREATE INDEX IF NOT EXISTS orders_user_created ON orders(user_id,created_at);
            """);
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(connectionString);
        db.Open();
        Execute(db, null, "PRAGMA foreign_keys=ON;");
        return db;
    }
    private static SqliteCommand Command(SqliteConnection db, SqliteTransaction? tx, string sql, params (string, object?)[] values)
    {
        var command = db.CreateCommand(); command.Transaction = tx; command.CommandText = sql;
        foreach (var (key, value) in values) command.Parameters.AddWithValue(key, value ?? DBNull.Value);
        return command;
    }
    private static int Execute(SqliteConnection db, SqliteTransaction? tx, string sql, params (string, object?)[] values)
    { using var command = Command(db, tx, sql, values); return command.ExecuteNonQuery(); }
    private static string Now() => DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset Date(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
    private static ApiException Missing() => new(404, "not_found", "记录不存在。");
    private static string Id(string value) => Guid.TryParse(value, out var id) ? id.ToString("N")
        : throw new ApiException(400, "invalid_request_id", "请求编号必须为 UUID。");

    public string RegisterUser(string username, byte[] passwordHash, byte[] salt, int iterations)
    {
        string id = Guid.NewGuid().ToString("N"); using var db = Open();
        try { Execute(db, null, "INSERT INTO users(id,username,password_hash,salt,iterations) VALUES($id,$name,$hash,$salt,$iterations)",
            ("$id", id), ("$name", username), ("$hash", passwordHash), ("$salt", salt), ("$iterations", iterations)); }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        { throw new ApiException(409, "username_exists", "该用户名已被使用。"); }
        return id;
    }
    public UserRecord? FindUser(string username)
    {
        using var db = Open(); using var command = Command(db, null,
            "SELECT id,username,password_hash,salt,iterations FROM users WHERE username=$name", ("$name", username));
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(reader.GetString(0), reader.GetString(1), (byte[])reader[2], (byte[])reader[3], reader.GetInt32(4)) : null;
    }
    public void CreateSession(string userId, string tokenHash, DateTimeOffset expiresAt)
    {
        using var db = Open(); Execute(db, null, "INSERT INTO sessions(token_hash,user_id,expires_at) VALUES($hash,$user,$expiry)",
            ("$hash", tokenHash), ("$user", userId), ("$expiry", expiresAt.ToString("O", CultureInfo.InvariantCulture)));
    }
    public string? FindSession(string tokenHash, DateTimeOffset now)
    {
        using var db = Open(); using var command = Command(db, null,
            "SELECT user_id FROM sessions WHERE token_hash=$hash AND expires_at>$now",
            ("$hash", tokenHash), ("$now", now.ToString("O", CultureInfo.InvariantCulture)));
        return command.ExecuteScalar() as string;
    }
    public void RevokeSession(string tokenHash)
    { using var db = Open(); Execute(db, null, "DELETE FROM sessions WHERE token_hash=$hash", ("$hash", tokenHash)); }
    public AccountRecord GetAccount(string userId)
    { using var db = Open(); return Account(db, null, userId); }
    private static AccountRecord Account(SqliteConnection db, SqliteTransaction? tx, string userId)
    {
        using var command = Command(db, tx, "SELECT username,balance,reserved FROM users WHERE id=$user", ("$user", userId));
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2)) : throw Missing();
    }
    public IReadOnlyList<LedgerRecord> GetLedger(string userId)
    {
        using var db = Open(); using var command = Command(db, null,
            "SELECT kind,amount,balance,created_at,reference FROM ledger WHERE user_id=$user ORDER BY id DESC LIMIT 200", ("$user", userId));
        using var reader = command.ExecuteReader(); var rows = new List<LedgerRecord>();
        while (reader.Read()) rows.Add(new(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), Date(reader.GetString(3)), reader.GetString(4)));
        return rows;
    }
    private static void Ledger(SqliteConnection db, SqliteTransaction tx, string userId, string kind, long amount, string reference)
    {
        Execute(db, tx, "INSERT INTO ledger(user_id,kind,amount,balance,created_at,reference) VALUES($user,$kind,$amount,$balance,$time,$ref)",
            ("$user", userId), ("$kind", kind), ("$amount", amount), ("$balance", Account(db, tx, userId).Balance), ("$time", Now()), ("$ref", reference));
    }

    public OrderRecord CreateOrder(string userId, string requestId, Plan plan)
    {
        requestId = Id(requestId); using var db = Open(); using var tx = db.BeginTransaction();
        var existing = ReadOrder(db, tx, "user_id=$user AND request_id=$request", ("$user", userId), ("$request", requestId));
        if (existing is not null)
        {
            if (existing.PlanId != plan.Id) throw new ApiException(409, "request_conflict", "同一请求编号不能用于其他充值计划。");
            tx.Commit(); return existing;
        }
        _ = Account(db, tx, userId);
        var order = new OrderRecord(Guid.NewGuid().ToString("N"), userId, requestId, plan.Id, plan.Name,
            plan.Credits, plan.AmountFen, plan.AfdianPlanId, Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant(), "pending", DateTimeOffset.UtcNow);
        Execute(db, tx, """
            INSERT INTO orders(id,user_id,request_id,plan_id,name,credits,amount_fen,afdian_plan_id,custom_id,status,created_at)
            VALUES($id,$user,$request,$plan,$name,$credits,$amount,$afdian,$custom,'pending',$created)
            """, ("$id", order.Id), ("$user", userId), ("$request", requestId), ("$plan", plan.Id), ("$name", plan.Name),
            ("$credits", plan.Credits), ("$amount", plan.AmountFen), ("$afdian", plan.AfdianPlanId), ("$custom", order.CustomId),
            ("$created", order.CreatedAt.ToString("O", CultureInfo.InvariantCulture)));
        tx.Commit(); return order;
    }
    private const string OrderColumns = "id,user_id,request_id,plan_id,name,credits,amount_fen,afdian_plan_id,custom_id,status,created_at";
    private static OrderRecord Order(SqliteDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
        r.GetInt32(5), r.GetInt32(6), r.GetString(7), r.GetString(8), r.GetString(9), Date(r.GetString(10)));
    private static OrderRecord? ReadOrder(SqliteConnection db, SqliteTransaction? tx, string where, params (string, object?)[] values)
    {
        using var command = Command(db, tx, "SELECT " + OrderColumns + " FROM orders WHERE " + where, values);
        using var reader = command.ExecuteReader(); return reader.Read() ? Order(reader) : null;
    }
    public OrderRecord GetOrder(string userId, string id)
    { using var db = Open(); return ReadOrder(db, null, "user_id=$user AND id=$id", ("$user", userId), ("$id", Id(id))) ?? throw Missing(); }
    internal OrderRecord GetMockOrder(string id)
    { using var db = Open(); return ReadOrder(db, null, "id=$id", ("$id", Id(id))) ?? throw Missing(); }
    public OrderRecord? FindOrderByCustomId(string customId)
    { using var db = Open(); return ReadOrder(db, null, "custom_id=$custom", ("$custom", customId)); }
    public IReadOnlyList<OrderRecord> GetOrders(string userId)
    {
        using var db = Open(); using var command = Command(db, null, "SELECT " + OrderColumns + " FROM orders WHERE user_id=$user ORDER BY created_at DESC LIMIT 100", ("$user", userId));
        using var reader = command.ExecuteReader(); var rows = new List<OrderRecord>(); while (reader.Read()) rows.Add(Order(reader)); return rows;
    }
    public OrderRecord CreditOrder(string orderId, string providerTradeNo, int verifiedAmountFen, string verifiedPlanId, string verifiedCustomId)
    {
        if (providerTradeNo.Length is < 1 or > 128 || providerTradeNo.Any(char.IsControl))
            throw new ApiException(400, "invalid_payment", "支付订单信息无效。");
        using var db = Open(); using var tx = db.BeginTransaction();
        var order = ReadOrder(db, tx, "id=$id", ("$id", Id(orderId))) ?? throw Missing();
        if (order.AmountFen != verifiedAmountFen || order.AfdianPlanId != verifiedPlanId || order.CustomId != verifiedCustomId)
            throw new ApiException(409, "payment_mismatch", "支付金额、方案或订单绑定不匹配，未增加积分。");
        // A replay must never re-credit a paid, refund-reserved or refunded order.
        if (order.Status != "pending") { tx.Commit(); return order; }
        using (var duplicate = Command(db, tx, "SELECT id FROM orders WHERE provider_trade_no=$trade", ("$trade", providerTradeNo)))
            if (duplicate.ExecuteScalar() is not null) throw new ApiException(409, "payment_used", "该支付订单已被处理。");
        Execute(db, tx, "UPDATE orders SET status='paid',provider_trade_no=$trade WHERE id=$id", ("$trade", providerTradeNo), ("$id", order.Id));
        Execute(db, tx, "UPDATE users SET balance=balance+$amount WHERE id=$user", ("$amount", order.Credits), ("$user", order.UserId));
        Ledger(db, tx, order.UserId, "recharge", order.Credits, "payment:" + providerTradeNo);
        tx.Commit(); return order with { Status = "paid" };
    }

    public RefundRecord HoldRefund(string orderId, string requestId)
    {
        orderId = Id(orderId); requestId = Id(requestId);
        using var db = Open(); using var tx = db.BeginTransaction();
        var existing = ReadRefund(db, tx, requestId);
        if (existing is not null)
        {
            if (existing.OrderId != orderId) throw new ApiException(409, "request_conflict", "退款请求编号已用于其他订单。");
            tx.Commit(); return existing;
        }
        var order = ReadOrder(db, tx, "id=$id", ("$id", orderId)) ?? throw Missing();
        if (order.Status != "paid") throw new ApiException(409, "refund_state", "只能冻结已支付且未进入退款的订单。");
        if (Execute(db, tx, "UPDATE users SET reserved=reserved+$credits WHERE id=$user AND balance-reserved >= $credits",
            ("$credits", order.Credits), ("$user", order.UserId)) != 1)
            throw new ApiException(409, "refund_insufficient_credits", "可用积分不足以回收此订单积分；未做任何变更，请人工核对已消费部分后处理。");
        Execute(db, tx, "UPDATE orders SET status='refund_pending' WHERE id=$id", ("$id", orderId));
        Execute(db, tx, "INSERT INTO refunds(id,order_id,status,created_at) VALUES($id,$order,'pending',$time)",
            ("$id", requestId), ("$order", orderId), ("$time", Now()));
        Ledger(db, tx, order.UserId, "refund_hold", 0, "refund-hold:" + requestId);
        var record = ReadRefund(db, tx, requestId)!; tx.Commit(); return record;
    }
    public RefundRecord ResolveRefund(string requestId, bool platformRefundConfirmed)
    {
        requestId = Id(requestId); using var db = Open(); using var tx = db.BeginTransaction();
        var refund = ReadRefund(db, tx, requestId) ?? throw Missing();
        if (refund.Status != "pending") { tx.Commit(); return refund; }
        var order = ReadOrder(db, tx, "id=$id", ("$id", refund.OrderId))!;
        if (order.Status != "refund_pending") throw new ApiException(409, "refund_state", "退款状态不一致，请人工核对。");
        Execute(db, tx, "UPDATE users SET reserved=reserved-$credits,balance=balance-$debit WHERE id=$user",
            ("$credits", order.Credits), ("$debit", platformRefundConfirmed ? order.Credits : 0), ("$user", order.UserId));
        Execute(db, tx, "UPDATE orders SET status=$status WHERE id=$id", ("$status", platformRefundConfirmed ? "refunded" : "paid"), ("$id", order.Id));
        string status = platformRefundConfirmed ? "confirmed" : "cancelled";
        Execute(db, tx, "UPDATE refunds SET status=$status WHERE id=$id", ("$status", status), ("$id", requestId));
        Ledger(db, tx, order.UserId, platformRefundConfirmed ? "refund" : "refund_release", platformRefundConfirmed ? -order.Credits : 0,
            "refund-resolve:" + requestId);
        tx.Commit(); return refund with { Status = status };
    }
    private static RefundRecord? ReadRefund(SqliteConnection db, SqliteTransaction tx, string id)
    {
        using var command = Command(db, tx, "SELECT r.id,r.order_id,r.status,o.credits,r.created_at FROM refunds r JOIN orders o ON o.id=r.order_id WHERE r.id=$id", ("$id", id));
        using var r = command.ExecuteReader(); return r.Read() ? new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3), Date(r.GetString(4))) : null;
    }

    public (TranslationJob Job, bool Created) ReserveTranslation(string userId, string requestId, string bodyHash, string inputJson)
    {
        requestId = Id(requestId); using var db = Open(); using var tx = db.BeginTransaction();
        var existing = ReadTranslation(db, tx, userId, requestId);
        if (existing is not null)
        {
            if (IsPreCancelled(existing)) { tx.Commit(); return (existing, false); }
            if (existing.BodyHash != bodyHash) throw new ApiException(409, "request_conflict", "同一请求编号不能用于不同翻译内容。");
            tx.Commit(); return (existing, false);
        }
        if (Execute(db, tx, "UPDATE users SET reserved=reserved+1 WHERE id=$user AND balance-reserved>=1", ("$user", userId)) != 1)
            throw new ApiException(402, "insufficient_credits", "可用积分不足，请先充值。");
        string now = Now();
        Execute(db, tx, """
            INSERT INTO translations(id,user_id,body_hash,input_json,status,created_at,updated_at)
            VALUES($id,$user,$hash,$input,'queued',$time,$time)
            """, ("$id", requestId), ("$user", userId), ("$hash", bodyHash), ("$input", inputJson), ("$time", now));
        var job = ReadTranslation(db, tx, userId, requestId)!; tx.Commit(); return (job, true);
    }
    private static TranslationJob? ReadTranslation(SqliteConnection db, SqliteTransaction? tx, string userId, string id)
    {
        using var command = Command(db, tx, """
            SELECT t.id,t.user_id,t.body_hash,t.input_json,t.status,t.result_json,t.error_code,u.balance
            FROM translations t JOIN users u ON u.id=t.user_id WHERE t.user_id=$user AND t.id=$id
            """, ("$user", userId), ("$id", id));
        using var r = command.ExecuteReader(); return r.Read() ? new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
            r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), r.GetInt64(7)) : null;
    }
    public TranslationJob GetTranslation(string userId, string id)
    { using var db = Open(); return ReadTranslation(db, null, userId, Id(id)) ?? throw Missing(); }
    public static bool IsPreCancelled(TranslationJob job) => job.Status == "cancelled" && job.BodyHash.Length == 0;
    public TranslationJob CancelTranslation(string userId, string id)
    {
        id = Id(id); using var db = Open(); using var tx = db.BeginTransaction();
        var job = ReadTranslation(db, tx, userId, id);
        if (job is null)
        {
            _ = Account(db, tx, userId);
            // An explicit cancellation can arrive while PUT is still reading its
            // body. Retain its identity without reserving credit or storing input.
            Execute(db, tx, """
                INSERT INTO translations(id,user_id,body_hash,input_json,status,error_code,created_at,updated_at)
                VALUES($id,$user,'','{}','cancelled','cancelled',$time,$time)
                """, ("$id", id), ("$user", userId), ("$time", Now()));
        }
        else if (job.Status is "queued" or "running")
        {
            Execute(db, tx, "UPDATE users SET reserved=reserved-1 WHERE id=$user", ("$user", userId));
            Execute(db, tx, "UPDATE translations SET status='cancelled',result_json=NULL,error_code='cancelled',updated_at=$time WHERE user_id=$user AND id=$id",
                ("$time", Now()), ("$user", userId), ("$id", id));
        }
        var cancelled = ReadTranslation(db, tx, userId, id)!;
        tx.Commit(); return cancelled;
    }
    public bool StartTranslation(string userId, string id)
    {
        using var db = Open(); return Execute(db, null, "UPDATE translations SET status='running',updated_at=$time WHERE user_id=$user AND id=$id AND status='queued'",
            ("$time", Now()), ("$user", userId), ("$id", Id(id))) == 1;
    }
    public TranslationJob CompleteTranslation(string userId, string id, string resultJson, bool charge) =>
        SettleTranslation(userId, id, charge ? "succeeded" : "no_text", resultJson, null, charge);
    public TranslationJob FailTranslation(string userId, string id, string status, string errorCode)
    {
        if (status is not ("failed" or "cancelled")) throw new ArgumentException("Invalid terminal state.", nameof(status));
        return SettleTranslation(userId, id, status, null, errorCode, false);
    }
    private TranslationJob SettleTranslation(string userId, string id, string status, string? resultJson, string? errorCode, bool charge)
    {
        id = Id(id); using var db = Open(); using var tx = db.BeginTransaction();
        var job = ReadTranslation(db, tx, userId, id) ?? throw Missing();
        if (job.Status is not ("queued" or "running")) { tx.Commit(); return job; }
        Execute(db, tx, "UPDATE users SET reserved=reserved-1,balance=balance-$charge WHERE id=$user", ("$charge", charge ? 1 : 0), ("$user", userId));
        Execute(db, tx, "UPDATE translations SET status=$status,result_json=$result,error_code=$error,updated_at=$time WHERE user_id=$user AND id=$id",
            ("$status", status), ("$result", resultJson), ("$error", errorCode), ("$time", Now()), ("$user", userId), ("$id", id));
        if (charge) Ledger(db, tx, userId, "translation", -1, "translation:" + id);
        var settled = ReadTranslation(db, tx, userId, id)!; tx.Commit(); return settled;
    }
    public int RecoverTranslations()
    {
        using var db = Open(); using var tx = db.BeginTransaction(); var counts = new List<(string User, long Count)>();
        using (var command = Command(db, tx, "SELECT user_id,COUNT(*) FROM translations WHERE status IN ('queued','running') GROUP BY user_id"))
        using (var r = command.ExecuteReader()) while (r.Read()) counts.Add((r.GetString(0), r.GetInt64(1)));
        foreach (var (user, count) in counts)
            Execute(db, tx, "UPDATE users SET reserved=reserved-$count WHERE id=$user", ("$count", count), ("$user", user));
        int changed = Execute(db, tx, "UPDATE translations SET status='failed',error_code='server_restarted',updated_at=$now WHERE status IN ('queued','running')", ("$now", Now()));
        tx.Commit(); return changed;
    }
    public void PruneExpiredData()
    {
        using var db = Open(); using var tx = db.BeginTransaction();
        Execute(db, tx, "DELETE FROM sessions WHERE expires_at <= $now", ("$now", Now()));
        Execute(db, tx, """
            UPDATE translations SET input_json='{}',result_json=NULL,error_code='result_expired'
            WHERE updated_at < $before AND status NOT IN ('queued','running') AND (input_json<>'{}' OR result_json IS NOT NULL)
            """, ("$before", DateTimeOffset.UtcNow.AddDays(-7).ToString("O", CultureInfo.InvariantCulture)));
        tx.Commit();
    }
}
