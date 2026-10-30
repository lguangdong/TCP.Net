using System.Buffers.Binary;

namespace SocketTest.Protocol;

/// <summary>
/// 从字节流里"拼"出完整帧 —— 整个 Socket 编程最容易出错的地方。
///
/// 两条铁律：
///   1. 一次 Read 返回的字节数可能少于请求长度，必须循环读满；
///   2. 一次 Read 也可能跨好几条消息，所以帧的边界只能靠长度字段自己算。
///
/// 本类不是线程安全的：一个连接只允许一个读循环，天然串行，所以复用同一块 header 缓冲区。
/// </summary>
public sealed class FrameReader
{
    private readonly Stream _stream;
    private readonly byte[] _header = new byte[ProtocolConstants.HeaderSize];
    private readonly int _maxPayloadSize;

    public FrameReader(Stream stream, int maxPayloadSize = ProtocolConstants.MaxPayloadSize)
    {
        _stream = stream;
        _maxPayloadSize = maxPayloadSize;
    }

    /// <summary>
    /// 读一条完整帧。
    /// </summary>
    /// <returns>返回 null 表示对端已在消息边界上正常关闭连接（Read 返回 0）。</returns>
    public async ValueTask<Frame?> ReadFrameAsync(CancellationToken cancellationToken)
    {
        // 1) 读满 16 字节头
        if (!await ReadFullyAsync(_header, cancellationToken).ConfigureAwait(false))
        {
            return null; // 一个字节都没读到 = 对端正常关闭
        }

        // 2) 校验 + 取字段
        var magic = ProtocolConstants.Magic;
        if (_header[0] != magic[0] || _header[1] != magic[1])
        {
            throw new InvalidDataException("协议魔数不匹配，可能连到了非本协议的端口");
        }

        if (_header[2] != ProtocolConstants.Version)
        {
            throw new InvalidDataException($"不支持的协议版本: {_header[2]}");
        }

        var type = (MessageType)_header[3];
        var flags = BinaryPrimitives.ReadUInt16BigEndian(_header.AsSpan(4));
        var seq = BinaryPrimitives.ReadUInt32BigEndian(_header.AsSpan(6));
        var length = (int)BinaryPrimitives.ReadUInt32BigEndian(_header.AsSpan(10));

        if (length < 0 || length > _maxPayloadSize)
        {
            // 不校验长度 = 让对端用一个包头决定你分配多少内存
            throw new InvalidDataException($"帧长度非法: {length}（上限 {_maxPayloadSize}）");
        }

        // 3) 读满负载
        if (length == 0)
        {
            return new Frame(type, seq, ReadOnlyMemory<byte>.Empty, flags);
        }

        // 优化点（留给后续练习）：这里每条消息都 new 一次，高吞吐场景应改用
        // ArrayPool<byte>.Shared + IMemoryOwner，并让 Frame 负责归还。
        var payload = new byte[length];
        if (!await ReadFullyAsync(payload, cancellationToken).ConfigureAwait(false))
        {
            throw new EndOfStreamException("帧读到一半连接就断了");
        }

        return new Frame(type, seq, payload, flags);
    }

    /// <summary>循环读到 buffer 填满为止。</summary>
    /// <returns>false 表示"一个字节都没读就遇到 EOF"。</returns>
    private async ValueTask<bool> ReadFullyAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await _stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                if (offset == 0)
                {
                    return false;
                }

                throw new EndOfStreamException("帧读到一半连接就断了");
            }

            offset += read;
        }

        return true;
    }
}
