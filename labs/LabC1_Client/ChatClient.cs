using LabC1.Client.Connection;
using LabC1.Client.Heartbeat;
using LabC1.Client.Rpc;
using SocketTest.Protocol;

namespace LabC1.Client;

/// <summary>
/// 客户端门面：把"连接 / 解析 / RPC / 状态机 / 心跳"组装成一条自愈的连接循环。
///
/// 整个类的价值就在 <see cref="RunAsync"/> 那个 while 循环里：
///   连 → 握手 → 上线 → 等它断 → 退避 → 再连
/// 上层业务代码完全不用关心这些。
///
/// ⚠️ 所有事件都在 IO 线程触发，UI 层必须自己切回主线程。
/// </summary>
public sealed class ChatClient : IAsyncDisposable
{
    private readonly ClientOptions _options;
    private readonly ConnectionStateMachine _stateMachine = new();
    private readonly RpcClient _rpc = new();
    private readonly HeartbeatMonitor _heartbeat;
    private readonly CancellationTokenSource _shutdownCts = new();

    private ClientConnection? _connection;
    private Task? _runLoop;
    private string? _token;
    private bool _disposed;

    public ChatClient(ClientOptions options)
    {
        _options = options;

        _heartbeat = new HeartbeatMonitor(
            PingAsync,
            options.HeartbeatInterval,
            options.MaxMissedHeartbeats);

        _heartbeat.RttMeasured += milliseconds => RttMeasured?.Invoke(milliseconds);
        _heartbeat.Dead += () =>
        {
            Log?.Invoke("心跳连续丢失，判定连接已死，主动断开");
            _connection?.Abort("heartbeat timeout");
        };

        _stateMachine.StateChanged += (previous, next) => StateChanged?.Invoke(previous, next);
    }

    public string UserName => _options.UserName;

    public string? Token => _token;

    public ConnectionState State => _stateMachine.State;

    public bool IsOnline => _stateMachine.State == ConnectionState.Online;

    public long LastRttMs => _heartbeat.LastRttMs;

    public int ReconnectAttempt => _stateMachine.Attempt;

    public event Action<ConnectionState, ConnectionState>? StateChanged;

    public event Action<string>? Log;

    public event Action<TextMessage>? MessageReceived;

    public event Action<long>? RttMeasured;

    public void Start() => _runLoop ??= Task.Run(() => RunAsync(_shutdownCts.Token));

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            ClientConnection? connection = null;
            try
            {
                _stateMachine.Transition(ConnectionState.Connecting);

                connection = await ClientConnection.ConnectAsync(
                    _options.Host,
                    _options.Port,
                    _options.ConnectTimeout,
                    cancellationToken).ConfigureAwait(false);

                _connection = connection;
                _rpc.Attach(connection);
                connection.FrameReceived += OnFrameReceived;
                connection.Closed += OnConnectionClosed;

                // 握手：认证失败属于"不可重试"，要停下来提示用户
                _stateMachine.Transition(ConnectionState.Handshaking);
                var login = await _rpc.LoginAsync(
                    _options.UserName,
                    _options.Password,
                    _options.RequestTimeout,
                    cancellationToken).ConfigureAwait(false);

                if (!login.Success)
                {
                    Log?.Invoke($"登录失败：{login.Error}（不会重试，请检查凭据）");
                    _stateMachine.Transition(ConnectionState.Disconnected);
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                _token = login.Token;
                _stateMachine.Transition(ConnectionState.Online);
                _stateMachine.ResetBackoff();
                _heartbeat.Start();
                Log?.Invoke($"已上线（token={_token}）");

                // 等这条连接死亡：可能是对端关闭、读异常、心跳超时
                await connection.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log?.Invoke($"连接失败：{ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                await _heartbeat.StopAsync().ConfigureAwait(false);
                _rpc.Detach();

                if (connection is not null)
                {
                    connection.FrameReceived -= OnFrameReceived;
                    connection.Closed -= OnConnectionClosed;
                    _connection = null;
                    await connection.DisposeAsync().ConfigureAwait(false);
                }
            }

            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (_stateMachine.State != ConnectionState.Disconnected)
            {
                _stateMachine.Transition(ConnectionState.Reconnecting);
            }

            var delay = _stateMachine.NextBackoff();
            Log?.Invoke($"{delay.TotalSeconds:0.0} 秒后重连（第 {_stateMachine.Attempt} 次）");

            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        if (_stateMachine.State != ConnectionState.Disconnected)
        {
            _stateMachine.Transition(ConnectionState.Disconnected);
        }
    }

    private Task PingAsync(CancellationToken cancellationToken)
        => _rpc.PingAsync(_options.PingTimeout, cancellationToken);

    private void OnFrameReceived(Frame frame)
    {
        // 响应帧已经被 RpcClient 消费掉了；这里只处理"服务端主动推来的"消息
        switch (frame.Type)
        {
            case MessageType.TextMessage:
            {
                var message = PayloadCodec.FromUtf8<TextMessage>(frame.Payload.Span);
                if (message is not null)
                {
                    MessageReceived?.Invoke(message);
                }

                break;
            }

            case MessageType.ServerNotice:
            {
                var notice = PayloadCodec.FromUtf8<ServerNotice>(frame.Payload.Span);
                if (notice is not null)
                {
                    Log?.Invoke($"[服务器/{notice.Level}] {notice.Text}");
                }

                break;
            }
        }
    }

    private void OnConnectionClosed(Exception? error)
    {
        // 让在途请求立刻失败，不要等到超时
        _rpc.Detach(new ConnectionClosedException(error?.Message ?? "连接已关闭"));
        Log?.Invoke(error is null ? "连接已关闭" : $"连接异常断开：{error.Message}");
    }

    public Task<string?> EchoAsync(string text, CancellationToken cancellationToken = default)
        => _rpc.EchoAsync(text, _options.RequestTimeout, cancellationToken);

    public async Task SendChatAsync(string text, CancellationToken cancellationToken = default)
    {
        var connection = _rpc.Connection ?? throw new ConnectionClosedException("当前未连接");
        if (!IsOnline)
        {
            throw new InvalidOperationException($"当前状态 {State}，不能发消息");
        }

        // 无应答的消息 seq 用 0
        await connection.SendAsync(
            MessageType.TextMessage,
            0,
            PayloadCodec.ToUtf8(new TextMessage(_options.UserName, text, DateTimeOffset.Now)),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync()
    {
        _shutdownCts.Cancel();

        if (_runLoop is not null)
        {
            try
            {
                await _runLoop.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch
            {
                // 停止阶段忽略
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopAsync().ConfigureAwait(false);
        await _heartbeat.DisposeAsync().ConfigureAwait(false);
        _rpc.Dispose();
        _shutdownCts.Dispose();
    }
}
