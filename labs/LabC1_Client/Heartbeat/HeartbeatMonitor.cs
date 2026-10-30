using System.Diagnostics;

namespace LabC1.Client.Heartbeat;

/// <summary>
/// 心跳监控：定期 Ping，连续丢失就判定连接已死。
///
/// 为什么不能靠 TCP KeepAlive：默认要 2 小时才探测。而移动网络切换、NAT 超时、
/// 服务端进程假死，这些场景下"TCP 连接仍然 ESTABLISHED"是个假象 —— 半开连接。
/// 应用层心跳是唯一可靠的手段。
/// </summary>
public sealed class HeartbeatMonitor : IAsyncDisposable
{
    private readonly Func<CancellationToken, Task> _ping;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _retryDelay;
    private readonly int _maxMissed;

    private CancellationTokenSource? _cts;
    private Task? _loop;

    public HeartbeatMonitor(
        Func<CancellationToken, Task> ping,
        TimeSpan interval,
        int maxMissed = 3,
        TimeSpan? retryDelay = null)
    {
        _ping = ping;
        _interval = interval;
        _maxMissed = Math.Max(1, maxMissed);
        _retryDelay = retryDelay ?? TimeSpan.FromSeconds(1);
    }

    /// <summary>最近一次心跳往返耗时，用于展示"网络良好/较差"。</summary>
    public long LastRttMs { get; private set; }

    public int TotalPings { get; private set; }

    public int TotalMissed { get; private set; }

    public bool IsBeating => _loop is { IsCompleted: false };

    public event Action<long>? RttMeasured;

    /// <summary>判定连接已死。订阅方应主动 Abort 连接，让状态机去重连。</summary>
    public event Action? Dead;

    public void Start()
    {
        if (IsBeating)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    public async ValueTask StopAsync()
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        var loop = Interlocked.Exchange(ref _loop, null);

        if (cts is not null)
        {
            cts.Cancel();
        }

        if (loop is not null)
        {
            try
            {
                await loop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch
            {
                // 停止阶段的异常忽略
            }
        }

        cts?.Dispose();
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(_interval, cancellationToken).ConfigureAwait(false);

                var missed = 0;
                while (missed < _maxMissed)
                {
                    var stopwatch = Stopwatch.StartNew();
                    try
                    {
                        TotalPings++;
                        await _ping(cancellationToken).ConfigureAwait(false);
                        stopwatch.Stop();
                        LastRttMs = stopwatch.ElapsedMilliseconds;
                        RttMeasured?.Invoke(LastRttMs);
                        break;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception)
                    {
                        missed++;
                        TotalMissed++;
                        if (missed < _maxMissed)
                        {
                            await Task.Delay(_retryDelay, cancellationToken).ConfigureAwait(false);
                        }
                    }
                }

                if (missed >= _maxMissed)
                {
                    Dead?.Invoke();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
