using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace Mugonzei.Server;

public static class AdminAccountActions
{
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record Change(string? Action, int? Delta, string? Reason);

    public static void Map(RouteGroupBuilder authorized) =>
        authorized.MapPost("/accounts/{id}/actions", (string id, Change input, HttpRequest request, Store store) =>
            store.ApplyAccountAction(id, AdminIssuance.RequestKey(request), input.Action, input.Delta, input.Reason));
}

internal sealed record AccountActionResult(string Action, int Delta, bool Disabled, long Balance, long Reserved,
    long Available, bool AlreadyApplied);

public sealed partial class Store
{
    private static void InitializeAccountManagement(SqliteConnection db) => Execute(db, null, """
        CREATE TABLE IF NOT EXISTS account_suspensions (
          user_id TEXT PRIMARY KEY REFERENCES users(id), created_at TEXT NOT NULL) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS admin_account_actions (
          sequence INTEGER PRIMARY KEY AUTOINCREMENT, request_id TEXT NOT NULL UNIQUE,
          user_id TEXT NOT NULL REFERENCES users(id), action TEXT NOT NULL CHECK(action IN ('adjust','suspend','restore')),
          delta INTEGER NOT NULL, reason TEXT NOT NULL, created_at TEXT NOT NULL, balance_after INTEGER NOT NULL,
          CHECK((action='adjust' AND delta BETWEEN -1000000 AND 1000000 AND delta!=0) OR (action!='adjust' AND delta=0)));
        CREATE INDEX IF NOT EXISTS account_actions_user_sequence ON admin_account_actions(user_id,sequence);
        CREATE INDEX IF NOT EXISTS sessions_user ON sessions(user_id);
        """);

    private static bool Suspended(SqliteConnection db, SqliteTransaction tx, string id)
    {
        using var cmd = Command(db, tx, "SELECT 1 FROM account_suspensions WHERE user_id=$id", ("$id", id));
        return cmd.ExecuteScalar() is not null;
    }

    private static ApiException AccountDisabled() => new(403, "account_disabled", "账号已停用，请联系管理员。");

    // Re-check inside the write transaction for requests that already passed bearer authentication.
    private static void RequireEnabled(SqliteConnection db, SqliteTransaction tx, string id)
    {
        if (Suspended(db, tx, id)) throw AccountDisabled();
    }

    internal AccountActionResult ApplyAccountAction(string id, Guid key, string? action, int? delta, string? reason)
    {
        id = Id(id);
        if (reason is null || reason.Any(char.IsControl) || (reason = reason.Trim()).Length is < 1 or > 200
            || action is not ("adjust" or "suspend" or "restore") || delta is null
            || (action == "adjust" ? delta is 0 or < -1000000 or > 1000000 : delta != 0))
            throw new ApiException(400, "invalid_account_action", "请填写 1–200 字操作原因；调整积分需填写非零整数，每次最多 100 万分。");
        int amount = delta.Value;
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: false);
        var account = Account(db, tx, id);
        bool disabled = Suspended(db, tx, id);
        bool replay;
        string requestId = key.ToString("D");
        using (var cmd = Command(db, tx,
            "SELECT user_id,action,delta,reason FROM admin_account_actions WHERE request_id=$key", ("$key", requestId)))
        using (var row = cmd.ExecuteReader())
        {
            replay = row.Read();
            if (replay && (row.GetString(0) != id || row.GetString(1) != action || row.GetInt32(2) != amount || row.GetString(3) != reason))
                throw new ApiException(409, "account_action_conflict", "该请求编号已用于不同操作，请刷新账户明细后再操作。");
        }
        if (!replay)
        {
            string now = Now();
            if (action == "adjust")
            {
                if (amount < 0 && account.Balance - account.Reserved < -(long)amount)
                    throw new ApiException(409, "insufficient_available_credits", "可用积分不足，无法扣减已预留的积分。");
                if (amount > 0 && account.Balance > long.MaxValue - amount)
                    throw new ApiException(409, "credit_limit", "积分余额已达到上限。");
                Execute(db, tx, "UPDATE users SET balance=balance+$delta WHERE id=$id", ("$delta",amount), ("$id",id));
                Ledger(db, tx, id, "admin_adjustment", amount, "admin-adjust:" + requestId);
                account = account with { Balance = checked(account.Balance + amount) };
            }
            else
            {
                bool target = action == "suspend";
                if (target == disabled)
                    throw new ApiException(409, "account_state_conflict", target ? "账号已停用，请刷新详情。" : "账号已启用，请刷新详情。");
                if (target)
                {
                    Execute(db, tx, "INSERT INTO account_suspensions(user_id,created_at) VALUES($id,$now)", ("$id",id), ("$now",now));
                    Execute(db, tx, "DELETE FROM sessions WHERE user_id=$id", ("$id",id));
                }
                else Execute(db, tx, "DELETE FROM account_suspensions WHERE user_id=$id", ("$id",id));
                disabled = target;
            }
            Execute(db, tx, """
                INSERT INTO admin_account_actions(request_id,user_id,action,delta,reason,created_at,balance_after)
                VALUES($key,$id,$action,$delta,$reason,$now,$balance)
                """, ("$key",requestId), ("$id",id), ("$action",action), ("$delta",amount), ("$reason",reason), ("$now",now), ("$balance",account.Balance));
        }
        tx.Commit();
        return new(action, amount, disabled, account.Balance, account.Reserved, account.Balance - account.Reserved, replay);
    }
}
