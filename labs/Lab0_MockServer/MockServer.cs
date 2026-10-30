using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using SocketTest.Protocol;

namespace Lab0.MockServer;

/// <summary>
/// 够用的假服务端：能接客、能回包、能主动推、能定时踢人。
/// 它不是用来学服务端架构的，只是客户端的"可控网络环境"。
/// </summary>
internal sealed class MockServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly ConcurrentDictionary<Guid, ClientSession> _sessions = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;

    public MockServer(int port)
    {
        _listener = new TcpListener(IPAddress.Any, port);
        Port = port;
    }

    public int Port { get; private set; }

    public FaultInjector Fault { get; } = new();

    public int OnlineCount => _sessions.Count;

    public ICollection<ClientSession> Sessions => _sessions.Values;

    public event Action<string>? Logging;

    public void Log(string message) => Logging?.Invoke($"[{DateTime.Now:HH:mm:ss}] {message}");

    public void Start()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient tcp;
            try
            {
                tcp = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log($"[错误] Accept 失败: {ex.Message}");
                continue;
            }

            var session = new ClientSession(tcp, this);
            _sessions[session.Id] = session;
            Log($"新连接 {session.RemoteEndPoint}，在线 {_sessions.Count}");

            _ = RunSessionAsync(session, cancellationToken);
        }
    }

    private async Task RunSessionAsync(ClientSession session, CancellationToken cancellationToken)
    {
        try
        {
            await session.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sessions.TryRemove(session.Id, out _);
            session.Dispose();
            Log($"断开 {session.RemoteEndPoint}，在线 {_sessions.Count}");
        }
    }

    public async Task BroadcastTextAsync(TextMessage message, CancellationToken cancellationToken)
    {
        foreach (var session in _sessions.Values)
        {
            if (!session.IsAuthenticated)
            {
                continue;
            }

            try
            {
                await session.SendTextAsync(message, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log($"[广播失败] {session.Describe()}: {ex.Message}");
            }
        }
    }

    public async Task BroadcastNoticeAsync(string text, CancellationToken cancellationToken)
    {
        foreach (var session in _sessions.Values)
        {
            try
            {
                await session.SendNoticeAsync(text, "info", cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log($"[通告失败] {session.Describe()}: {ex.Message}");
            }
        }
    }

    /// <summary>模拟服务端重启 / 强制踢人。客户端的重连逻辑就靠这个验证。</summary>
    public int KickAll()
    {
        var count = 0;
        foreach (var session in _sessions.Values)
        {
            session.Close();
            count++;
        }

        Log($"[踢人] 已断开 {count} 个连接");
        return count;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();

        foreach (var session in _sessions.Values)
        {
            session.Close();
        }

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch
            {
                // 退出时忽略
            }
        }

        _cts.Dispose();
    }
}
