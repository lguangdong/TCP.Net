using System.Text;

namespace Lab0.MockServer;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        var port = 9000;
        if (args.Length > 0 && int.TryParse(args[0], out var parsed))
        {
            port = parsed;
        }

        await using var server = new MockServer(port);
        server.Logging += Console.WriteLine;
        server.Start();

        Console.WriteLine("=== Lab0 Mock 服务器 ===");
        Console.WriteLine($"监听 0.0.0.0:{server.Port}（本机客户端连 127.0.0.1:{server.Port}）");
        PrintHelp();

        var quit = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            quit.Cancel();
        };

        while (!quit.IsCancellationRequested)
        {
            var line = Console.ReadLine();
            if (line is null)
            {
                break;
            }

            if (!HandleCommand(server, line, quit))
            {
                break;
            }
        }

        Console.WriteLine("正在关闭服务器…");
        return 0;
    }

    /// <returns>false 表示退出。</returns>
    private static bool HandleCommand(MockServer server, string line, CancellationTokenSource quit)
    {
        var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return true;
        }

        var command = parts[0].ToLowerInvariant();
        var argument = parts.Length > 1 ? parts[1] : string.Empty;

        switch (command)
        {
            case "help":
                PrintHelp();
                return true;

            case "status":
                Console.WriteLine($"在线 {server.OnlineCount} 人，故障参数: {server.Fault}");
                foreach (var session in server.Sessions)
                {
                    Console.WriteLine($"  - {session.Describe()}  {session.RemoteEndPoint}");
                }

                return true;

            case "drop":
                server.Fault.DropPercent = ParseInt(argument, 0, 100, "丢包率");
                Console.WriteLine($"丢包率 = {server.Fault.DropPercent}%");
                return true;

            case "delay":
                server.Fault.DelayMs = ParseInt(argument, 0, 60_000, "延迟");
                Console.WriteLine($"延迟 = {server.Fault.DelayMs}ms");
                return true;

            case "jitter":
                server.Fault.JitterMs = ParseInt(argument, 0, 60_000, "抖动");
                Console.WriteLine($"抖动 = {server.Fault.JitterMs}ms");
                return true;

            case "reset":
                server.Fault.Reset();
                Console.WriteLine("故障参数已重置");
                return true;

            case "push":
                if (string.IsNullOrWhiteSpace(argument))
                {
                    Console.WriteLine("用法: push <文本>");
                    return true;
                }

                Push(server, argument);
                return true;

            case "kill":
                server.KickAll();
                Console.WriteLine("提示：客户端应当自动进入重连状态");
                return true;

            case "quit":
            case "exit":
                quit.Cancel();
                return false;

            default:
                Console.WriteLine($"未知命令: {command}");
                PrintHelp();
                return true;
        }
    }

    private static void Push(MockServer server, string text)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await server.BroadcastNoticeAsync(text, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[push 失败] {ex.Message}");
            }
        });
    }

    private static int ParseInt(string text, int min, int max, string name)
    {
        if (!int.TryParse(text, out var value) || value < min || value > max)
        {
            Console.WriteLine($"{name}必须是 {min}-{max} 之间的整数，已忽略");
            return min;
        }

        return value;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            命令:
              status          查看在线连接与故障参数
              drop <0-100>    设置请求丢包率（模拟弱网）
              delay <ms>      设置固定响应延迟
              jitter <ms>     设置随机抖动
              reset           重置全部故障参数
              push <文本>     向所有客户端推一条服务端通告
              kill            断开所有连接（模拟服务重启）
              help            显示本帮助
              quit            退出
            """);
    }
}
