using System.Net.Sockets;
using SocketTest.Protocol;

namespace Lab0.MockServer;

/// <summary>
/// 一个客户端连接。一个会话 = 一个读循环 + 一把发送锁。
/// </summary>
internal sealed class ClientSession : IDisposable
{
    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly FrameReader _reader;
    private readonly MockServer _server;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private bool _disposed;

    public Guid Id { get; } = Guid.NewGuid();

    public string RemoteEndPoint { get; }

    public string? UserName { get; private set; }

    public bool IsAuthenticated => UserName is not null;

    public ClientSession(TcpClient tcp, MockServer server)
    {
        _tcp = tcp;
        _tcp.NoDelay = true;
        _stream = tcp.GetStream();
        _reader = new FrameReader(_stream);
        _server = server;
        RemoteEndPoint = tcp.Client.RemoteEndPoint?.ToString() ?? "unknown";
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var frame = await _reader.ReadFrameAsync(cancellationToken).ConfigureAwait(false);
                if (frame is null)
                {
                    return; // 客户端正常关闭
                }

                // 故障注入：先决定要不要"吃掉"这个请求
                if (_server.Fault.ShouldDrop())
                {
                    _server.Log($"[丢包] {Describe()} 的 {frame.Value.Type} 被丢弃");
                    continue;
                }

                await _server.Fault.ApplyDelayAsync(cancellationToken).ConfigureAwait(false);
                await HandleAsync(frame.Value, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 服务器关闭，正常退出
        }
        catch (EndOfStreamException)
        {
            // 客户端在帧中间断开
        }
        catch (Exception ex)
        {
            _server.Log($"[错误] {Describe()} 读循环异常: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task HandleAsync(Frame frame, CancellationToken cancellationToken)
    {
        switch (frame.Type)
        {
            case MessageType.Ping:
                // 关键：Pong 必须带回 Ping 的 seq，客户端靠它配对 + 算 RTT
                await SendAsync(MessageType.Pong, frame.Seq, EmptyPayload.Value, cancellationToken).ConfigureAwait(false);
                break;

            case MessageType.EchoRequest:
                await SendAsync(MessageType.EchoResponse, frame.Seq, frame.Payload, cancellationToken).ConfigureAwait(false);
                break;

            case MessageType.LoginRequest:
                await HandleLoginAsync(frame, cancellationToken).ConfigureAwait(false);
                break;

            case MessageType.TextMessage:
                await HandleTextAsync(frame, cancellationToken).ConfigureAwait(false);
                break;

            default:
                await SendNoticeAsync($"未知消息类型: {(byte)frame.Type}", "warn", cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private async Task HandleLoginAsync(Frame frame, CancellationToken cancellationToken)
    {
        var request = PayloadCodec.FromUtf8<LoginRequest>(frame.Payload.Span);
        if (request is null || string.IsNullOrWhiteSpace(request.UserName))
        {
            await SendAsync(
                MessageType.LoginResponse,
                frame.Seq,
                PayloadCodec.ToUtf8(new LoginResponse(false, null, "登录请求格式错误")),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        // 故意留一个"密码错误"的分支，用来验证客户端是否会停止重连
        if (request.Password == "wrong")
        {
            await SendAsync(
                MessageType.LoginResponse,
                frame.Seq,
                PayloadCodec.ToUtf8(new LoginResponse(false, null, "用户名或密码错误")),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        UserName = request.UserName;
        var token = Guid.NewGuid().ToString("N");

        await SendAsync(
            MessageType.LoginResponse,
            frame.Seq,
            PayloadCodec.ToUtf8(new LoginResponse(true, token, null)),
            cancellationToken).ConfigureAwait(false);

        _server.Log($"{Describe()} 登录成功，在线 {_server.OnlineCount} 人");
    }

    private async Task HandleTextAsync(Frame frame, CancellationToken cancellationToken)
    {
        if (!IsAuthenticated)
        {
            await SendNoticeAsync("请先登录", "error", cancellationToken).ConfigureAwait(false);
            return;
        }

        var message = PayloadCodec.FromUtf8<TextMessage>(frame.Payload.Span);
        if (message is null)
        {
            return;
        }

        // 服务端不信任客户端给的 From，用会话里的用户名覆盖
        await _server.BroadcastTextAsync(
            new TextMessage(UserName!, message.Text, DateTimeOffset.Now),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task SendAsync(
        MessageType type,
        uint seq,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        // 发送锁：多个业务线程可能同时广播，必须串行写同一个 Socket
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await FrameCodec.WriteAsync(_stream, type, seq, payload, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public Task SendNoticeAsync(string text, string level, CancellationToken cancellationToken)
        => SendAsync(
            MessageType.ServerNotice,
            0,
            PayloadCodec.ToUtf8(new ServerNotice(text, level)),
            cancellationToken);

    public Task SendTextAsync(TextMessage message, CancellationToken cancellationToken)
        => SendAsync(MessageType.TextMessage, 0, PayloadCodec.ToUtf8(message), cancellationToken);

    /// <summary>服务端主动断开（模拟被踢 / 服务重启）。</summary>
    public void Close()
    {
        try
        {
            _tcp.Close();
        }
        catch
        {
            // 忽略关闭时的异常
        }
    }

    public string Describe() => UserName ?? RemoteEndPoint;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Close();
        _stream.Dispose();
        _tcp.Dispose();
        _sendLock.Dispose();
    }
}
