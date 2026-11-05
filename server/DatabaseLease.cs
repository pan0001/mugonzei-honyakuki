using System.Text.Json;

namespace Mugonzei.Server;

public sealed class DatabaseLease : IDisposable
{
    private readonly FileStream handle;
    public DatabaseLease(string databasePath)
    {
        string path = Path.GetFullPath(databasePath) + ".lock";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try { handle = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new InvalidOperationException("数据库已由另一个服务或管理命令占用；仅支持单进程，请先停止占用该数据库的服务。"); }
    }
    public void Dispose() => handle.Dispose();
}

public static class RefundCommands
{
    public static int Run(string[] args)
    {
        try
        {
            string Value(string key)
            {
                int index = Array.IndexOf(args, key);
                if (index < 0 || index + 1 >= args.Length) throw new ArgumentException("缺少退款命令参数。");
                return args[index + 1];
            }
            if (args.Length < 2 || args[1] is not ("hold" or "confirm" or "release"))
                throw new ArgumentException("用法：refund hold --order UUID --request UUID；refund confirm --request UUID --platform-refund-confirmed；refund release --request UUID。");
            string database = Environment.GetEnvironmentVariable("MUGONZEI_DATABASE") ?? "data/mugonzei.db";
            if (!File.Exists(database)) throw new InvalidOperationException("指定数据库不存在；管理命令不会创建新数据库。");
            using var lease = new DatabaseLease(database);
            var store = new Store(database);
            string request = Value("--request");
            RefundRecord result;
            if (args[1] == "hold") result = store.HoldRefund(Value("--order"), request);
            else
            {
                if (args[1] == "confirm" && !args.Contains("--platform-refund-confirmed"))
                    throw new ArgumentException("必须先在爱发电完成人工资金退款，再传 --platform-refund-confirmed；本命令不会向爱发电退款。");
                result = store.ResolveRefund(request, args[1] == "confirm");
            }
            Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            return 0;
        }
        catch (Exception error) when (error is ApiException or ArgumentException or InvalidOperationException)
        { Console.Error.WriteLine(error.Message); return 1; }
    }
}
