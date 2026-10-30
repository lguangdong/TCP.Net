namespace SocketTest.Protocol;

/// <summary>
/// 一条完整的应用层消息。注意：它只是"已经拼好的一条消息"，
/// 从字节流到 Frame 的过程由 <see cref="FrameReader"/> 负责。
/// </summary>
public readonly struct Frame
{
    public MessageType Type { get; }

    /// <summary>请求序号。响应帧会原样带回请求的序号，这是"请求-响应配对"的唯一依据。</summary>
    public uint Seq { get; }

    public ushort Flags { get; }

    public ReadOnlyMemory<byte> Payload { get; }

    public Frame(MessageType type, uint seq, ReadOnlyMemory<byte> payload, ushort flags = 0)
    {
        Type = type;
        Seq = seq;
        Payload = payload;
        Flags = flags;
    }

    public override string ToString()
        => $"[{Type} seq={Seq} len={Payload.Length}]";
}
