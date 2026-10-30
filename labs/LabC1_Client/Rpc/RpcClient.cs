using System.Collections.Concurrent;
using LabC1.Client.Connection;
using SocketTest.Protocol;

namespace LabC1.Client.Rpc;

/// <summary>
/// 把"发一条消息，等一条回复"封装成 async/await。
///
/// 核心机制：seq 自增 → 发出前把 TaskCompletionSource 存进字典 → 收包里按 seq 取出来完成它。
/// 三个必须做对的地方：
///   1. TaskCompletionSource 必须带 RunContinuationsAsynchronously，否则回包线程会直接
///      跑进调用方的后续代码，容易死锁 / 卡住 IO 线程；
///   2. 超时必须从字典里移除，否则每超时一次就泄漏一个对象；
///   3. 连接断开时要把所有 pending 全部失败掉，否则那些 await 永远等下去。
/// </summary>
public sealed class RpcClient : IDisposable
{
    private readonly ConcurrentDictionary<uint, PendingRequest> _pending = new();
    private ClientConnection? _connection;
    private int _seq;
    private bool _disposed;

    public ClientConnection? Connection => _connection;

    public int PendingCount => _pending.Count;

    public void Attach(ClientConnection connection)
    {
        Detach(new ConnectionClosedException("连接已切换"));
        _connection = connection;
        connection.FrameReceived += OnFrameReceived;
    }

    /// <summary>解绑连接，并让所有在途请求立刻失败。</summary>
    public void Detach(Exception? reason = null)
    {
        var connection = Interlocked.Exchange(ref _connection, null);
        if (connection is not null)
        {
            connection.FrameReceived -= OnFrameReceived;
        }

        FailAll(reason ?? new ConnectionClosedException("连接已断开"));
    }

    private void FailAll(Exception reason)
    {
        foreach (var key in _pending.Keys)
        {
            if (_pending.TryRemove(key, out var pending))
            {
                pending.Fail(reason);
            }
        }
    }

    /// <summary>收包线程进入：只处理"能按 seq 配对上的响应"，其它帧忽略（交给业务层）。</summary>
    private void OnFrameReceived(Frame frame)
    {
        if (!_pending.TryGetValue(frame.Seq, out var pending))
        {
            return;
        }

        if (pending.ResponseType != frame.Type)
        {
            return;
        }

        if (_pending.TryRemove(frame.Seq, out var removed))
        {
            removed.Complete(frame);
        }
    }

    /// <summary>原始字节版请求，心跳这种无负载的消息用它。</summary>
    public async Task<Frame> RequestAsync(
        MessageType requestType,
        MessageType responseType,
        ReadOnlyMemory<byte> payload,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var connection = _connection ?? throw new ConnectionClosedException("当前没有可用连接");

        var seq = (uint)Interlocked.Increment(ref _seq);
        var pending = new PendingRequest(responseType);
        _pending[seq] = pending;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await connection.SendAsync(requestType, seq, payload, cancellationToken).ConfigureAwait(false);
            return await pending.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"请求 {requestType} 超时（{timeout.TotalMilliseconds:0} ms）");
        }
        finally
        {
            _pending.TryRemove(seq, out _);
        }
    }

    /// <summary>带 JSON 负载的泛型请求。</summary>
    public async Task<TResponse> RequestAsync<TRequest, TResponse>(
        MessageType requestType,
        MessageType responseType,
        TRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var frame = await RequestAsync(
            requestType,
            responseType,
            PayloadCodec.ToUtf8(request),
            timeout,
            cancellationToken).ConfigureAwait(false);

        return PayloadCodec.FromUtf8<TResponse>(frame.Payload.Span)
               ?? throw new InvalidDataException($"响应 {responseType} 负载为空");
    }

    public Task PingAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        => RequestAsync(MessageType.Ping, MessageType.Pong, EmptyPayload.Value, timeout, cancellationToken);

    public async Task<string?> EchoAsync(string text, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var response = await RequestAsync<EchoPayload, EchoPayload>(
            MessageType.EchoRequest,
            MessageType.EchoResponse,
            new EchoPayload(text),
            timeout,
            cancellationToken).ConfigureAwait(false);

        return response.Text;
    }

    public Task<LoginResponse> LoginAsync(
        string userName,
        string password,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
        => RequestAsync<LoginRequest, LoginResponse>(
            MessageType.LoginRequest,
            MessageType.LoginResponse,
            new LoginRequest(userName, password),
            timeout,
            cancellationToken);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Detach();
    }

    private sealed class PendingRequest
    {
        private readonly TaskCompletionSource<Frame> _tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public PendingRequest(MessageType responseType) => ResponseType = responseType;

        public MessageType ResponseType { get; }

        public Task<Frame> Task => _tcs.Task;

        public void Complete(Frame frame) => _tcs.TrySetResult(frame);

        public void Fail(Exception exception) => _tcs.TrySetException(exception);
    }
}
