using System.Buffers.Binary;
using System.Text;

namespace TelltaleD3DMeshEditor.Core;

// Sequential little-endian binary reader used by all parsers (.d3dmesh / .skl / .d3dtx).
// Maintains a current position and validates bounds on every read.
public sealed class DataReader
{
    private readonly byte[] _data;

    public DataReader(byte[] data)
    {
        _data = data;
    }

    public int Position { get; private set; }
    public int Length => _data.Length;

    public void Seek(int position)
    {
        if (position < 0 || position > _data.Length)
        {
            throw new EndOfStreamException($"Offset is outside the file: 0x{position:X}");
        }

        Position = position;
    }

    public void Skip(int count)
    {
        if (count < 0)
        {
            throw new InvalidDataException($"Negative skip: {count}");
        }

        Seek(Position + count);
    }

    public byte ReadByte()
    {
        Ensure(1);
        return _data[Position++];
    }

    public sbyte ReadSByte() => unchecked((sbyte)ReadByte());

    public ushort ReadUInt16()
    {
        Ensure(2);
        var value = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(Position, 2));
        Position += 2;
        return value;
    }

    public ushort ReadUInt16BigEndian()
    {
        Ensure(2);
        var value = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(Position, 2));
        Position += 2;
        return value;
    }

    public short ReadInt16()
    {
        Ensure(2);
        var value = BinaryPrimitives.ReadInt16LittleEndian(_data.AsSpan(Position, 2));
        Position += 2;
        return value;
    }

    public uint ReadUInt32()
    {
        Ensure(4);
        var value = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(Position, 4));
        Position += 4;
        return value;
    }

    public int ReadInt32()
    {
        Ensure(4);
        var value = BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(Position, 4));
        Position += 4;
        return value;
    }

    public float ReadFloat()
    {
        Ensure(4);
        var value = BinaryPrimitives.ReadSingleLittleEndian(_data.AsSpan(Position, 4));
        Position += 4;
        return value;
    }

    public float ReadHalf()
    {
        Ensure(2);
        var half = BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(Position, 2)));
        Position += 2;
        return (float)half;
    }

    public string ReadAscii(int length)
    {
        Ensure(length);
        var value = Encoding.ASCII.GetString(_data, Position, length).TrimEnd('\0');
        Position += length;
        return value;
    }

    public byte[] Slice(int offset, int length)
    {
        if (offset < 0 || length < 0 || offset + length > _data.Length)
        {
            throw new EndOfStreamException($"Slice is outside the file: 0x{offset:X}+0x{length:X}");
        }

        return _data.AsSpan(offset, length).ToArray();
    }

    public uint PeekUInt32()
    {
        Ensure(4);
        return BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(Position, 4));
    }

    public uint PeekUInt32(int offset)
    {
        if (offset < 0 || offset + 4 > _data.Length)
        {
            throw new EndOfStreamException($"Offset is outside the file: 0x{offset:X}");
        }

        return BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(offset, 4));
    }

    public ushort PeekUInt16(int offset)
    {
        if (offset < 0 || offset + 2 > _data.Length)
        {
            throw new EndOfStreamException($"Offset is outside the file: 0x{offset:X}");
        }

        return BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(offset, 2));
    }

    public float PeekFloat(int offset)
    {
        if (offset < 0 || offset + 4 > _data.Length)
        {
            throw new EndOfStreamException($"Offset is outside the file: 0x{offset:X}");
        }

        return BinaryPrimitives.ReadSingleLittleEndian(_data.AsSpan(offset, 4));
    }

    public (float X, float Y, float Z) ReadVec3()
    {
        return (ReadFloat(), ReadFloat(), ReadFloat());
    }

    private void Ensure(int count)
    {
        if (Position + count > _data.Length)
        {
            throw new EndOfStreamException($"Unexpected end of file at offset 0x{Position:X}");
        }
    }
}
