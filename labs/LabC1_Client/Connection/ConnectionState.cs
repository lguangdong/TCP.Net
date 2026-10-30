namespace LabC1.Client.Connection;

/// <summary>
/// 客户端连接状态。任何网络操作都必须先问一句"现在是什么状态"。
/// </summary>
public enum ConnectionState
{
    /// <summary>未连接（初始状态，或用户主动下线）</summary>
    Disconnected,

    /// <summary>正在建立 TCP 连接</summary>
    Connecting,

    /// <summary>TCP 已通，正在认证 / 握手</summary>
    Handshaking,

    /// <summary>可用状态，只有这里才允许发业务消息</summary>
    Online,

    /// <summary>连接已断，正在等待退避时间</summary>
    Reconnecting,
}
