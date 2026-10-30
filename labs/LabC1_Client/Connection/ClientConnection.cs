using System.Net.Sockets;
using System.Threading.Channels;
using SocketTest.Protocol;

namespace LabC1.Client.Connection;

/// <summary>
/// 一条 TCP 连接 = 两个循环：
///   读循环：FrameReader 拼包 → 抛出 FrameReceived 事件
///   写循环：从 Channel 里取字节 → 串行写 Socket
///
/// 为什么发送一定要走队列：UI 线程发聊天、心跳定时器发 Ping、后台回调发 ACK，
/// 三个线程同时写同一个 Socket 会把数据交叉切碎。单写者是硬约束。
/// </summary>
public sealed class ClientConnection : IAsyncDisposable
{
    private const int SendQueueCapacity = 1024;

    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly FrameReader _reader;
    private readonly Channel<byte[]> _sendQueue;
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Task? _readLoop;
    private Task? _sendLoop;
    private bool _disposed;

    public string RemoteEndPoint { get; }

    /// <summary>每收到一条完整帧触发一次。注意：**在 IO 线程触发**，不要在这里碰 UI。</summary>
    public event Action<Frame>? FrameReceived;

    /// <summary>连接关闭时触发，参数为异常（null 表示正常关闭）。</summary>
    public event Action<Exception?>? Closed;

    /// <summary>连接结束（无论正常还是异常）都会完成，用于等待"连接已死"。</summary>
    public Task Completion => _closed.Task;

    public bool IsClosed => _closed.Task.IsCompleted;

    private ClientConnection(TcpClient tcp)
    {
        _tcp = tcp;
        _tcp.NoDelay = true; // 关掉 Nagle，小包立即发，降低 RTT
        _stream = tcp.GetStream();
        _reader = new FrameReader(_stream);
        RemoteEndPoint = tcp.Client.RemoteEndPoint?.ToString() ?? "unknown";

        _sendQueue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(SendQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait, // 队列满了就反压发送方，而不是丢数据
            SingleReader = true,
        });
    }

    public static async Task<ClientConnection> ConnectAsync(
        string host,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var tcp = new TcpClient();

        // 连接超时：单独一个 CTS，不能污染外层的 cancellationToken
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await tcp.ConnectAsync(host, port, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            tcp.Dispose();
            throw new TimeoutException($"连接 {host}:{port} 超时（{timeout.TotalMilliseconds:0} ms）");
        }
        catch
        {
            tcp.Dispose();
            throw;
        }

        var connection = new ClientConnection(tcp);
        connection.Start();
        return connection;
    }

    private void Start()
    {
        _readLoop = Task.Run(ReadLoopAsync);
        _sendLoop = Task.Run(SendLoopAsync);
    }

    /// <summary>发送一条消息（进队列后立即返回，真正写盘由写循环负责）。</summary>
    public ValueTask SendAsync(
        MessageType type,
        uint seq,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
        => _sendQueue.Writer.WriteAsync(FrameCodec.Encode(type, seq, payload.Span), cancellationToken);

    private async Task ReadLoopAsync()
    {
        Exception? error = null;
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var frame = await _reader.ReadFrameAsync(_cts.Token).ConfigureAwait(false);
                if (frame is null)
                {
                    break; // 对端正常关闭
                }

                try
                {
                    FrameReceived?.Invoke(frame.Value);
                }
                catch (Exception ex)
                {
                    // 单个订阅者抛异常不能拖垮读循环
                    Console.Error.WriteLine($"[帧处理异常] {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 主动关闭
        }
        catch (Exception ex)
        {
            error = ex;
        }
        finally
        {
            Shutdown(error);
        }
    }

    private async Task SendLoopAsync()
    {
        try
        {
            while (await _sendQueue.Reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
            {
                while (_sendQueue.Reader.TryRead(out var buffer))
                {
                    await _stream.WriteAsync(buffer, _cts.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 主动关闭
        }
        catch (Exception ex)
        {
            Shutdown(ex);
        }
    }

    /// <summary>外部强制断开（例如心跳超时）。</summary>
    public void Abort(string reason) => Shutdown(new IOException(reason));

    private void Shutdown(Exception? error)
    {
        // 读循环、写循环、外部 Abort 都可能进来，必须幂等
        if (!_closed.TrySetResult())
        {
            return;
        }

        _sendQueue.Writer.TryComplete();
        _cts.Cancel();

        try
        {
            _tcp.Client?.Shutdown(SocketShutdown.Both);
        }
        catch
        {
            // 对端已断时会抛，忽略
        }

        try
        {
            _tcp.Close();
        }
        catch
        {
            // 忽略
        }

        Closed?.Invoke(error);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Shutdown(null);

        var loops = new[] { _readLoop, _sendLoop }
            .Where(task => task is not null)
            .Select(task => task!)
            .ToArray();

        try
        {
            await Task.WhenAll(loops).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch
        {
            // 关闭阶段的异常没有价值
        }

        _stream.Dispose();
        _tcp.Dispose();
        _cts.Dispose();
    }
}
