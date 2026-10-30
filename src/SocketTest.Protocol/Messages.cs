namespace SocketTest.Protocol;

// 这是 Lab 阶段用的"业务协议"：负载统一用 UTF-8 JSON，方便肉眼调试。
// 真实项目里换成 MessagePack / protobuf 时，只需要替换 PayloadCodec，帧格式完全不动。

/// <summary>登录请求。密码故意用明文，是为了演示"为什么必须上 TLS"。</summary>
public sealed record LoginRequest(string UserName, string Password);

/// <summary>登录响应。Token 是客户端要持久化保存的东西。</summary>
public sealed record LoginResponse(bool Success, string? Token, string? Error);

/// <summary>回显负载。</summary>
public sealed record EchoPayload(string Text);

/// <summary>聊天文本消息。seq 固定为 0，因为它不需要应答。</summary>
public sealed record TextMessage(string From, string Text, DateTimeOffset SentAt);

/// <summary>服务端通告（含错误提示）。Level: info / warn / error。</summary>
public sealed record ServerNotice(string Text, string Level = "info");

/// <summary>空负载，避免到处写 Array.Empty。</summary>
public static class EmptyPayload
{
    public static readonly ReadOnlyMemory<byte> Value = ReadOnlyMemory<byte>.Empty;
}
