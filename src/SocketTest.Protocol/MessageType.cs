namespace SocketTest.Protocol;

/// <summary>
/// 应用层消息类型。0-99 为系统消息，100+ 留给业务。
/// </summary>
public enum MessageType : byte
{
    Unknown = 0,

    /// <summary>心跳请求（无负载）</summary>
    Ping = 1,

    /// <summary>心跳响应（seq 与 Ping 相同，客户端用它算 RTT）</summary>
    Pong = 2,

    /// <summary>回显请求，负载 EchoPayload</summary>
    EchoRequest = 10,

    /// <summary>回显响应，负载 EchoPayload</summary>
    EchoResponse = 11,

    /// <summary>登录请求，负载 LoginRequest</summary>
    LoginRequest = 20,

    /// <summary>登录响应，负载 LoginResponse</summary>
    LoginResponse = 21,

    /// <summary>文本消息（无请求序号，seq=0），负载 TextMessage</summary>
    TextMessage = 30,

    /// <summary>服务端通告，负载 ServerNotice</summary>
    ServerNotice = 40,
}

public static class MessageTypeExtensions
{
    /// <summary>这个类型是否属于"响应"（需要按 seq 交给 RpcClient 配对）。</summary>
    public static bool IsResponse(this MessageType type) => type switch
    {
        MessageType.Pong => true,
        MessageType.EchoResponse => true,
        MessageType.LoginResponse => true,
        _ => false,
    };
}
