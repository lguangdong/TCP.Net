using System.Buffers.Binary;
using System.Text.Json;

namespace SocketTest.Protocol;

/// <summary>
/// 帧的编码（对象 → 字节）与头部解析（字节 → 字段）。
/// 解码的"拼包"逻辑在 <see cref="FrameReader"/>，因为那里需要 Stream。
/// </summary>
public static class FrameCodec
{
    /// <summary>把一条消息编码成可直接写入 Socket 的字节数组。</summary>
    public static byte[] Encode(MessageType type, uint seq, ReadOnlySpan<byte> payload, ushort flags = 0)
    {
        if (payload.Length > ProtocolConstants.MaxPayloadSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(payload), $"负载 {payload.Length} 字节，超过上限 {ProtocolConstants.MaxPayloadSize}");
        }

        var buffer = new byte[ProtocolConstants.HeaderSize + payload.Length];
        WriteHeader(buffer, type, seq, payload.Length, flags);
        payload.CopyTo(buffer.AsSpan(ProtocolConstants.HeaderSize));
        return buffer;
    }

    /// <summary>JSON 负载的便捷重载。</summary>
    public static byte[] EncodeJson<T>(MessageType type, uint seq, T value, ushort flags = 0)
        => Encode(type, seq, PayloadCodec.ToUtf8(value), flags);

    public static void WriteHeader(Span<byte> destination, MessageType type, uint seq, int payloadLength, ushort flags = 0)
    {
        var magic = ProtocolConstants.Magic;
        destination[0] = magic[0];
        destination[1] = magic[1];
        destination[2] = ProtocolConstants.Version;
        destination[3] = (byte)type;
        BinaryPrimitives.WriteUInt16BigEndian(destination[4..], flags);
        BinaryPrimitives.WriteUInt32BigEndian(destination[6..], seq);
        BinaryPrimitives.WriteUInt32BigEndian(destination[10..], (uint)payloadLength);
        BinaryPrimitives.WriteUInt16BigEndian(destination[14..], 0);
    }

    /// <summary>
    /// 从一段缓冲区里尝试取出一条完整帧。
    /// 返回 false 表示"数据还不够（半包）"，调用方应继续读；
    /// 数据非法则抛 <see cref="InvalidDataException"/>，调用方应断开连接。
    /// </summary>
    /// <param name="consumed">本次消费了多少字节</param>
    public static bool TryParse(ReadOnlySpan<byte> buffer, out Frame frame, out int consumed)
    {
        frame = default;
        consumed = 0;

        if (buffer.Length < ProtocolConstants.HeaderSize)
        {
            return false;
        }

        var magic = ProtocolConstants.Magic;
        if (buffer[0] != magic[0] || buffer[1] != magic[1])
        {
            throw new InvalidDataException("协议魔数不匹配，可能连到了非本协议的端口");
        }

        if (buffer[2] != ProtocolConstants.Version)
        {
            throw new InvalidDataException($"不支持的协议版本: {buffer[2]}");
        }

        var type = (MessageType)buffer[3];
        var flags = BinaryPrimitives.ReadUInt16BigEndian(buffer[4..]);
        var seq = BinaryPrimitives.ReadUInt32BigEndian(buffer[6..]);
        var length = (int)BinaryPrimitives.ReadUInt32BigEndian(buffer[10..]);

        if (length < 0 || length > ProtocolConstants.MaxPayloadSize)
        {
            throw new InvalidDataException(
                $"帧长度非法: {length}（上限 {ProtocolConstants.MaxPayloadSize}）");
        }

        var total = ProtocolConstants.HeaderSize + length;
        if (buffer.Length < total)
        {
            return false;
        }

        frame = new Frame(type, seq, buffer.Slice(ProtocolConstants.HeaderSize, length).ToArray(), flags);
        consumed = total;
        return true;
    }

    /// <summary>把一条消息直接写进流（内部走单次 WriteAsync，不做拆包）。</summary>
    public static async ValueTask WriteAsync(
        Stream stream,
        MessageType type,
        uint seq,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        var buffer = Encode(type, seq, payload.Span);
        await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>DTO 与 UTF-8 字节的互转。换 MessagePack / protobuf 时只需要改这一个文件。</summary>
public static class PayloadCodec
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static byte[] ToUtf8<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);

    public static T? FromUtf8<T>(ReadOnlySpan<byte> payload)
        => payload.IsEmpty ? default : JsonSerializer.Deserialize<T>(payload, Options);
}
