using System.Buffers.Binary;
using System.Text;

namespace GameTranslatorOverlay.CorpusTool.Unity;

internal sealed class BinaryCursor(byte[] data, int start, int length, bool bigEndian)
{
    private readonly int _end = checked(start + length);

    public BinaryCursor(byte[] data, bool bigEndian) : this(data, 0, data.Length, bigEndian)
    {
    }

    public bool BigEndian { get; set; } = bigEndian;

    public int Position { get; set; } = start;

    public int Remaining => _end - Position;

    public int Start { get; } = start;

    public byte ReadByte() => Take(1)[0];

    public bool ReadBool() => ReadByte() != 0;

    public short ReadInt16() => BigEndian ? BinaryPrimitives.ReadInt16BigEndian(Take(2)) : BinaryPrimitives.ReadInt16LittleEndian(Take(2));

    public ushort ReadUInt16() => BigEndian ? BinaryPrimitives.ReadUInt16BigEndian(Take(2)) : BinaryPrimitives.ReadUInt16LittleEndian(Take(2));

    public int ReadInt32() => BigEndian ? BinaryPrimitives.ReadInt32BigEndian(Take(4)) : BinaryPrimitives.ReadInt32LittleEndian(Take(4));

    public uint ReadUInt32() => BigEndian ? BinaryPrimitives.ReadUInt32BigEndian(Take(4)) : BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

    public long ReadInt64() => BigEndian ? BinaryPrimitives.ReadInt64BigEndian(Take(8)) : BinaryPrimitives.ReadInt64LittleEndian(Take(8));

    public void Skip(int count) => Take(count);

    public ReadOnlySpan<byte> ReadSpan(int count) => Take(count);

    public string ReadCString(int maxLength = 4096)
    {
        var span = data.AsSpan(Position, Remaining);
        var zero = span[..Math.Min(span.Length, maxLength + 1)].IndexOf((byte)0);
        if (zero < 0) throw new InvalidDataException("Nie znaleziono końca napisu w nagłówku pliku.");
        var text = Encoding.UTF8.GetString(span[..zero]);
        Position += zero + 1;
        return text;
    }

    public void Align(int alignment)
    {
        var relative = Position - Start;
        var padding = (alignment - relative % alignment) % alignment;
        if (padding > Remaining) throw new InvalidDataException("Wyrównanie wychodzi poza dane.");
        Position += padding;
    }

    private ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || count > Remaining)
        {
            throw new InvalidDataException($"Dane kończą się przedwcześnie (potrzeba {count} B, zostało {Math.Max(0, Remaining)} B).");
        }
        var span = data.AsSpan(Position, count);
        Position += count;
        return span;
    }
}
