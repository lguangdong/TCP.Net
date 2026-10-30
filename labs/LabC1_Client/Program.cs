using System.Text;
using LabC1.Client.Connection;
using SocketTest.Protocol;

namespace LabC1.Client;

internal static class Program
{
    private static readonly MainThreadDispatcher Dispatcher = new();
    private static readonly CancellationTokenSource Quit = new();

    private static ChatClient? _client;

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        ClientOptions options;
        try
        {
            options = ParseArgs(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        Console.WriteLine("=== Lab-C1 Socket 客户端 ===");
        Console.WriteLine($"目标 {options.Host}:{options.Port}  用户 {options.UserName}  模式 {(options.Demo ? "自动 Echo" : "交互")}");
        Console.WriteLine("输入 help 查看命令\n");

        _client = new ChatClient(options);

        // 关键：所有事件都在 IO 线程触发，统一 Post 到主线程再打印
        _client.StateChanged += (previous, next) =>
            Dispatcher.Post(() => Console.WriteLine($"  [状态] {previous} → {next}"));
        _client.Log += text => Dispatcher.Post(() => Console.WriteLine($"  [日志] {text}"));
        _client.MessageReceived += message =>
            Dispatcher.Post(() => Console.WriteLine($"  [消息] {message.From} {message.SentAt:HH:mm:ss}: {message.Text}"));

        _client.Start();

        _ = Task.Run(ReadInputLoop, Quit.Token);

        if (options.Demo)
        {
            _ = Task.Run(() => DemoLoopAsync(Quit.Token));
        }

        // 这就是控制台版的 "Unity Update"：只在这里碰控制台输出
        try
        {
            while (!Quit.IsCancellationRequested)
            {
                Dispatcher.Pump();
                await Task.Delay(50).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 退出
        }

        Dispatcher.Pump();
        Console.WriteLine("正在下线…");

        await _client.StopAsync().ConfigureAwait(false);
        await _client.DisposeAsync().ConfigureAwait(false);
        return 0;
    }

    private static void ReadInputLoop()
    {
        while (!Quit.IsCancellationRequested)
        {
            var line = Console.ReadLine();
            if (line is null)
            {
                Quit.Cancel();
                return;
            }

            Dispatcher.Post(() => HandleCommand(line));
        }
    }

    private static void HandleCommand(string line)
    {
        var client = _client;
        if (client is null)
        {
            return;
        }

        var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return;
        }

        var command = parts[0].ToLowerInvariant();
        var argument = parts.Length > 1 ? parts[1] : string.Empty;

        switch (command)
        {
            case "help":
                PrintHelp();
                break;

            case "state":
                Console.WriteLine(
                    $"  状态={client.State} 在线={client.IsOnline} RTT={client.LastRttMs}ms 重连次数={client.ReconnectAttempt}");
                break;

            case "echo":
                FireAndForget(async () =>
                {
                    var reply = await client.EchoAsync(argument).ConfigureAwait(false);
                    Dispatcher.Post(() => Console.WriteLine($"  [echo] {reply}"));
                });
                break;

            case "chat":
                FireAndForget(async () =>
                {
                    await client.SendChatAsync(argument).ConfigureAwait(false);
                });
                break;

            case "quit":
            case "exit":
                Quit.Cancel();
                break;

            default:
                Console.WriteLine($"  未知命令: {command}（help 查看）");
                break;
        }
    }

    /// <summary>fire-and-forget 必须自带 try/catch，否则异常会变成未观察异常。</summary>
    private static void FireAndForget(Func<Task> action)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await action().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Dispatcher.Post(() => Console.WriteLine($"  [失败] {ex.GetType().Name}: {ex.Message}"));
            }
        });
    }

    /// <summary>Demo 模式：每 2 秒打一次 Echo。服务端 kill 之后能直观看到重连恢复。</summary>
    private static async Task DemoLoopAsync(CancellationToken cancellationToken)
    {
        var client = _client;
        if (client is null)
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);

                if (!client.IsOnline)
                {
                    Dispatcher.Post(() => Console.WriteLine($"  [demo] 离线中（{client.State}），跳过本次 Echo"));
                    continue;
                }

                var reply = await client.EchoAsync($"tick {DateTime.Now:HH:mm:ss}").ConfigureAwait(false);
                Dispatcher.Post(() => Console.WriteLine($"  [demo] Echo 成功: {reply}"));
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Dispatcher.Post(() => Console.WriteLine($"  [demo] Echo 失败: {ex.GetType().Name}: {ex.Message}"));
            }
        }
    }

    private static ClientOptions ParseArgs(string[] args)
    {
        var options = new ClientOptions();

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--host" when i + 1 < args.Length:
                    options = options with { Host = args[++i] };
                    break;
                case "--port" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out var port))
                    {
                        throw new ArgumentException("--port 必须是数字");
                    }

                    options = options with { Port = port };
                    break;
                case "--user" when i + 1 < args.Length:
                    options = options with { UserName = args[++i] };
                    break;
                case "--password" when i + 1 < args.Length:
                    options = options with { Password = args[++i] };
                    break;
                case "--heartbeat" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out var seconds))
                    {
                        throw new ArgumentException("--heartbeat 必须是秒数");
                    }

                    options = options with { HeartbeatInterval = TimeSpan.FromSeconds(seconds) };
                    break;
                case "--demo":
                    options = options with { Demo = true };
                    break;
                case "-h":
                case "--help":
                    throw new ArgumentException(Usage);
                default:
                    throw new ArgumentException($"未知参数: {args[i]}\n{Usage}");
            }
        }

        return options;
    }

    private const string Usage = """
        用法: LabC1.Client [选项]
          --host <ip>           服务器地址，默认 127.0.0.1
          --port <port>         服务器端口，默认 9000
          --user <name>         用户名，默认 guest
          --password <pwd>      密码，默认 123456（填 wrong 可测试登录失败）
          --heartbeat <秒>      心跳间隔，默认 5
          --demo                每 2 秒自动 Echo 一次
        """;

    private static void PrintHelp()
    {
        Console.WriteLine("""
            命令:
              state          查看状态 / RTT / 重连次数
              echo <文本>    发一条需要应答的请求（验证 RPC 配对）
              chat <文本>    发一条聊天消息（服务端会广播给所有人）
              help           显示本帮助
              quit           退出
            """);
    }
}
