namespace Lab0.MockServer;

/// <summary>
/// 故障注入器。客户端调不好，90% 是因为"网络永远很好"。
/// 在服务端模拟丢包和延迟，比拿真机进电梯测试高效得多。
/// </summary>
internal sealed class FaultInjector
{
    /// <summary>丢包率 0-100，作用于"收到的请求"。</summary>
    public int DropPercent { get; set; }

    /// <summary>固定延迟（毫秒），在回包前等待。</summary>
    public int DelayMs { get; set; }

    /// <summary>随机抖动（毫秒），在固定延迟上再叠加 0..JitterMs。</summary>
    public int JitterMs { get; set; }

    public bool ShouldDrop()
        => DropPercent > 0 && Random.Shared.Next(1, 101) <= DropPercent;

    public async Task ApplyDelayAsync(CancellationToken cancellationToken)
    {
        if (DelayMs <= 0 && JitterMs <= 0)
        {
            return;
        }

        var delay = DelayMs + (JitterMs > 0 ? Random.Shared.Next(0, JitterMs + 1) : 0);
        if (delay > 0)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    public void Reset()
    {
        DropPercent = 0;
        DelayMs = 0;
        JitterMs = 0;
    }

    public override string ToString()
        => $"drop={DropPercent}%  delay={DelayMs}ms  jitter={JitterMs}ms";
}
