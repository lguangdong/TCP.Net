using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using SocketTest.Protocol;

namespace Lab1.PacketBoundary;

/// <summary>
/// 粘包 / 拆包实验。服务端和客户端跑在同一个进程里，用回环地址通信，跑完自动退出。
///
/// 为什么要做这个实验：
///   TCP 只保证"字节按顺序到达"，不保证"一次 Write 对应一次 Read，也不保证一条消息对应一次 Read"。
///   本程序把这两种情况人为制造出来，让你亲眼看到接收端是怎么一次一次"攒"字节的。
///
/// 运行方式：
///   dotnet run --project labs\Lab1_PacketBoundary            （跑全部）
///   dotnet run --project labs\Lab1_PacketBoundary -- split   （只跑某一种）
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        var mode = (args.Length > 0 ? args[0] : "all").ToLowerInvariant();

        Console.WriteLine("=== Lab1 粘包 / 拆包实验 ===");
        Console.WriteLine("观察两件事：");
        Console.WriteLine("  1) 一条帧『用了几次 ReadAsync』才凑齐");
        Console.WriteLine("  2) 一次 ReadAsync 拿到的字节里『能解析出几条帧』");
        Console.WriteLine();

        try
        {
            switch (mode)
            {
                case "normal":
                    await RunAsync("实验 1：正常情况（1 条消息 = 1 次 Write）", SendSingleAsync);
                    break;

                case "split":
                    await RunAsync("实验 2：拆包，切在【帧头中间】（16 字节头只发前 5 字节）",
                        stream => SendSplitAsync(stream, splitAt: 5));
                    await RunAsync("实验 3：拆包，切在【负载中间】（16 字节头 + 4 字节负载后再切）",
                        stream => SendSplitAsync(stream, splitAt: 20));
                    break;

                case "coalesce":
                    await RunAsync("实验 4：粘包（3 条消息拼成 1 次 Write）", SendCoalescedAsync);
                    break;

                case "all":
                    await RunAsync("实验 1：正常情况（1 条消息 = 1 次 Write）", SendSingleAsync);
                    await RunAsync("实验 2：拆包，切在【帧头中间】（16 字节头只发前 5 字节）",
                        stream => SendSplitAsync(stream, splitAt: 5));
                    await RunAsync("实验 3：拆包，切在【负载中间】（16 字节头 + 4 字节负载后再切）",
                        stream => SendSplitAsync(stream, splitAt: 20));
                    await RunAsync("实验 4：粘包（3 条消息拼成 1 次 Write）", SendCoalescedAsync);
                    break;

                default:
                    Console.WriteLine("用法: Lab1.PacketBoundary [normal|split|coalesce|all]");
                    return 1;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[实验出错] {ex}");
            return 1;
        }

        Console.WriteLine();
        Console.WriteLine("实验结束。");
        return 0;
    }

    /// <summary>
    /// 跑一次实验：起一个监听 → 客户端连上去 → 按指定方式发送 → 等服务端读完。
    /// </summary>
    private static async Task RunAsync(string title, Func<NetworkStream, Task> sender)
    {
        Console.WriteLine(new string('=', 72));
        Console.WriteLine(title);
        Console.WriteLine(new string('=', 72));

        // 端口传 0 = 让系统随便分配一个空闲端口，避免和你本机的 9000 冲突
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var receiverTask = Task.Run(async () =>
        {
            using var accepted = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
            await ReceiveLoopAsync(accepted.GetStream()).ConfigureAwait(false);
        });

        // NoDelay = true 关掉 Nagle 算法，保证"我 Write 几次，就真的分几次发出"
        // 否则系统可能把小包攒一会儿再一起发，实验现象就不稳定了
        using var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(IPAddress.Loopback, port).ConfigureAwait(false);

        var stream = client.GetStream();
        await sender(stream).ConfigureAwait(false);

        // 等数据真正落到内核缓冲区
        await Task.Delay(100).ConfigureAwait(false);

        // 半关闭：只关"发送"方向，告诉对方"我发完了"，但还能继续读
        // 如果不这么做，接收端会一直等下去（它无法区分"暂时没数据"和"发完了"）
        client.Client.Shutdown(SocketShutdown.Send);

        await receiverTask.ConfigureAwait(false);

        listener.Stop();
        client.Close();
        Console.WriteLine();
    }

    // ─────────────────────────── 发送端 ───────────────────────────

    /// <summary>1 条消息，1 次 Write。</summary>
    private static async Task SendSingleAsync(NetworkStream stream)
    {
        var frame = BuildFrame(MessageType.EchoRequest, 1, "hello world");

        Console.WriteLine($"  [发送] 一次 Write 发出 {frame.Length} 字节 = 16 字节头 + 11 字节负载");
        Console.WriteLine($"  [发送] {Hex(frame)}");

        await stream.WriteAsync(frame).ConfigureAwait(false);
    }

    /// <summary>
    /// 1 条消息，拆成 2 次 Write，中间停 400ms。
    /// 这是真实网络里最常见的拆包原因：大包被 MTU 切分，或者发送方自己分多次写。
    /// </summary>
    private static async Task SendSplitAsync(NetworkStream stream, int splitAt)
    {
        var frame = BuildFrame(MessageType.EchoRequest, 2, "hello world");
        var second = frame.Length - splitAt;

        Console.WriteLine($"  [发送] 一条帧共 {frame.Length} 字节，故意切成 {splitAt} + {second} 两次 Write，中间停 400ms");
        Console.WriteLine($"  [发送] 第一段: {Hex(frame.AsSpan(0, splitAt))}");
        Console.WriteLine($"  [发送] 第二段: {Hex(frame.AsSpan(splitAt))}");

        await stream.WriteAsync(frame.AsMemory(0, splitAt)).ConfigureAwait(false);
        await Task.Delay(400).ConfigureAwait(false);
        await stream.WriteAsync(frame.AsMemory(splitAt)).ConfigureAwait(false);
    }

    /// <summary>3 条消息，拼进一个 byte[]，1 次 Write。</summary>
    private static async Task SendCoalescedAsync(NetworkStream stream)
    {
        var frames = new[]
        {
            BuildFrame(MessageType.TextMessage, 1, "msg-1"),
            BuildFrame(MessageType.TextMessage, 2, "msg-2"),
            BuildFrame(MessageType.TextMessage, 3, "msg-3"),
        };

        var combined = new byte[frames.Sum(frame => frame.Length)];
        var offset = 0;
        foreach (var frame in frames)
        {
            Buffer.BlockCopy(frame, 0, combined, offset, frame.Length);
            offset += frame.Length;
        }

        Console.WriteLine($"  [发送] 3 条帧（21+21+21）拼成 {combined.Length} 字节，一次 Write 发出");
        Console.WriteLine($"  [发送] {Hex(combined)}");

        await stream.WriteAsync(combined).ConfigureAwait(false);
    }

    // ─────────────────────────── 接收端 ───────────────────────────

    /// <summary>
    /// 接收循环。这里故意**不用** FrameReader，把"攒字节 → 试着解析 → 不够就继续读"
    /// 的过程摊开给你看。生产代码里这段逻辑被封装进了 FrameReader。
    /// </summary>
    private static async Task ReceiveLoopAsync(NetworkStream stream)
    {
        var buffer = new byte[4096];   // 缓冲区 = 还没被消费掉的裸字节
        var buffered = 0;              // 缓冲区里现有多少字节
        var readsThisFrame = 0;        // 这条帧已经花了几次 ReadAsync
        var frameIndex = 0;

        while (true)
        {
            // 缓冲区被塞满却还没凑出一条帧 → 说明缓冲区比最大帧还小，这是配置错误
            // （真实代码里这个上限就是 ProtocolConstants.MaxFrameSize）
            if (buffered == buffer.Length)
            {
                throw new InvalidOperationException(
                    $"接收缓冲区只有 {buffer.Length} 字节，装不下一条完整帧。缓冲区必须 >= 单帧最大长度。");
            }

            // 注意：从 buffered 位置开始写，保留上一次没解析完的尾巴
            var read = await stream.ReadAsync(buffer.AsMemory(buffered)).ConfigureAwait(false);

            if (read == 0)
            {
                Console.WriteLine("  [接收] ReadAsync 返回 0 → 对端已关闭");
                break;
            }

            readsThisFrame++;
            buffered += read;

            Console.WriteLine($"  [接收] ReadAsync 返回 {read} 字节，缓冲区现在 {buffered} 字节");
            Console.WriteLine($"         {Hex(buffer.AsSpan(0, buffered))}");

            // 一次 Read 可能包含 0 条、1 条或多条完整帧 —— 必须循环试
            while (true)
            {
                if (buffered >= ProtocolConstants.HeaderSize)
                {
                    Console.WriteLine($"         └ 帧头 {DescribeHeader(buffer)}");
                }

                if (!FrameCodec.TryParse(buffer.AsSpan(0, buffered), out var frame, out var consumed))
                {
                    if (buffered > 0)
                    {
                        Console.WriteLine($"  [解析] ✗ 剩 {buffered} 字节凑不出完整帧 → 回去继续 Read");
                    }

                    break;
                }

                frameIndex++;

                var readNote = readsThisFrame switch
                {
                    0 => "与上一条帧来自同一批数据，0 次额外 Read",
                    1 => "1 次 Read 就拿到",
                    _ => $"{readsThisFrame} 次 Read 才凑齐",
                };

                Console.WriteLine(
                    $"  [解析] ✓ 第 {frameIndex} 条帧  type={frame.Type} seq={frame.Seq} " +
                    $"payload=\"{Encoding.UTF8.GetString(frame.Payload.Span)}\"  （{readNote}）");

                readsThisFrame = 0;

                // 关键一步：把已消费的字节丢掉，剩下的挪到缓冲区开头
                if (consumed < buffered)
                {
                    Buffer.BlockCopy(buffer, consumed, buffer, 0, buffered - consumed);
                }

                buffered -= consumed;
            }
        }
    }

    // ─────────────────────────── 工具 ───────────────────────────

    private static byte[] BuildFrame(MessageType type, uint seq, string text)
        => FrameCodec.Encode(type, seq, Encoding.UTF8.GetBytes(text));

    /// <summary>把 16 字节帧头拆成可读字段，方便和上面的 hex 一一对应。</summary>
    private static string DescribeHeader(ReadOnlySpan<byte> buffer)
    {
        var type = (MessageType)buffer[3];
        var seq = BinaryPrimitives.ReadUInt32BigEndian(buffer[6..]);
        var length = BinaryPrimitives.ReadUInt32BigEndian(buffer[10..]);

        return $"magic={buffer[0]:X2}{buffer[1]:X2}  ver={buffer[2]:X2}  " +
               $"type={buffer[3]:X2}({type})  seq={seq}  len={length}";
    }

    private static string Hex(ReadOnlySpan<byte> data, int max = 24)
    {
        var shown = Math.Min(data.Length, max);
        var builder = new StringBuilder(shown * 3);

        for (var i = 0; i < shown; i++)
        {
            if (i > 0)
            {
                builder.Append(' ');
            }

            builder.Append(data[i].ToString("X2"));
        }

        if (data.Length > shown)
        {
            builder.Append($" …");
        }

        return builder.ToString();
    }
}
