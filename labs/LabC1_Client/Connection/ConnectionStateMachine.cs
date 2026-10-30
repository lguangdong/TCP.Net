namespace LabC1.Client.Connection;

/// <summary>
/// 纯状态机：只管"允许怎么流转"和"下次重连等多久"，不碰 Socket。
/// 把它单独拎出来，是因为状态乱跳是客户端最隐蔽的 bug 来源。
/// </summary>
public sealed class ConnectionStateMachine
{
    private static readonly Dictionary<ConnectionState, ConnectionState[]> AllowedTransitions = new()
    {
        [ConnectionState.Disconnected] = [ConnectionState.Connecting],
        [ConnectionState.Connecting] = [ConnectionState.Handshaking, ConnectionState.Reconnecting, ConnectionState.Disconnected],
        [ConnectionState.Handshaking] = [ConnectionState.Online, ConnectionState.Reconnecting, ConnectionState.Disconnected],
        [ConnectionState.Online] = [ConnectionState.Reconnecting, ConnectionState.Disconnected],
        [ConnectionState.Reconnecting] = [ConnectionState.Connecting, ConnectionState.Disconnected],
    };

    private readonly TimeSpan _baseDelay = TimeSpan.FromSeconds(1);
    private readonly TimeSpan _maxDelay = TimeSpan.FromSeconds(30);
    private int _attempt;

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;

    /// <summary>参数：旧状态、新状态。</summary>
    public event Action<ConnectionState, ConnectionState>? StateChanged;

    public void Transition(ConnectionState next)
    {
        var previous = State;
        if (previous == next)
        {
            return;
        }

        if (!AllowedTransitions.TryGetValue(previous, out var allowed) || !allowed.Contains(next))
        {
            throw new InvalidOperationException($"非法的状态流转: {previous} → {next}");
        }

        State = next;
        StateChanged?.Invoke(previous, next);
    }

    public void ResetBackoff() => _attempt = 0;

    /// <summary>
    /// 下一次重连的等待时间：指数退避 + 抖动。
    /// 抖动很重要 —— 否则服务端重启后所有客户端会在同一毫秒涌入，把自己打死。
    /// </summary>
    public TimeSpan NextBackoff()
    {
        _attempt++;
        var exponent = Math.Min(_attempt - 1, 10);
        var milliseconds = Math.Min(
            _maxDelay.TotalMilliseconds,
            _baseDelay.TotalMilliseconds * Math.Pow(2, exponent));

        var jitter = 0.5 + (Random.Shared.NextDouble() * 0.5); // 0.5 ~ 1.0
        return TimeSpan.FromMilliseconds(milliseconds * jitter);
    }

    public int Attempt => _attempt;
}
