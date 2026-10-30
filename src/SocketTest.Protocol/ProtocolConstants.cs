namespace SocketTest.Protocol;

/// <summary>
/// 协议常量。所有数字都是"线上格式"，改动即为不兼容变更，必须升 Version。
///
/// 帧结构（共 16 字节头 + N 字节负载）：
///   偏移  长度  字段        说明
///   0     2     magic       固定 'S','K'（0x53 0x4B），用来识别"是不是本协议"
///   2     1     version     协议版本
///   3     1     msgType     消息类型
///   4     2     flags       保留位
///   6     4     seq         请求序号，响应必须原样带回
///   10    4     payloadLen  负载长度（大端）
///   14    2     reserved    保留
///   16    N     payload     负载，本 Lab 用 UTF-8 JSON
/// </summary>
public static class ProtocolConstants
{
    /// <summary>
    /// 帧头魔数 'S','K'。
    /// 这里用 byte[] 而不是 ReadOnlySpan 属性：C# 12 的 async 方法里不能放 ref struct 局部变量，
    /// 用 byte[] 才能在 await 之间安全持有。
    /// </summary>
    public static readonly byte[] Magic = { 0x53, 0x4B };

    public const byte Version = 1;

    public const int HeaderSize = 16;

    /// <summary>单帧最大长度。没有这个上限，一个 0x7FFFFFFF 的长度字段就能打爆内存。</summary>
    public const int MaxFrameSize = 64 * 1024;

    public const int MaxPayloadSize = MaxFrameSize - HeaderSize;
}
