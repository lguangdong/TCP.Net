namespace LabC1.Client;

/// <summary>客户端配置。所有超时都是"必须存在"的，没有超时的网络代码等于没有错误处理。</summary>
public sealed record ClientOptions
{
    public string Host { get; init; } = "127.0.0.1";

    public int Port { get; init; } = 9000;

    public string UserName { get; init; } = "guest";

    public string Password { get; init; } = "123456";

    /// <summary>TCP 连接超时。默认 5 秒，太短会在弱网下频繁误判。</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>单次请求-响应超时。</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>心跳间隔。真实项目 15~30 秒，这里为了演示用 5 秒。</summary>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>单次心跳超时。</summary>
    public TimeSpan PingTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>连续丢失几次心跳判定连接已死。</summary>
    public int MaxMissedHeartbeats { get; init; } = 3;

    /// <summary>Demo 模式：每 2 秒自动发一次 Echo，方便观察断线重连。</summary>
    public bool Demo { get; init; }
}
