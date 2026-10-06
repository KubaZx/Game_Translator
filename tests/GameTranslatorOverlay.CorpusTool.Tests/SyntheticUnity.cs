using System.Buffers.Binary;
using System.Text;

namespace GameTranslatorOverlay.CorpusTool.Tests;

internal static class Lz4Encoder
{
    public static byte[] LiteralsOnly(ReadOnlySpan<byte> data)
    {
        var output = new List<byte>();
        WriteToken(output, data.Length, 0);
        WriteLength(output, data.Length);
        output.AddRange(data.ToArray());
        return output.ToArray();
    }

    public static byte[] WithRepeat(byte[] head, int offset, int matchLength, byte[] tail)
    {
        var output = new List<byte>();
        WriteToken(output, head.Length, matchLength - 4);
        WriteLength(output, head.Length);
        output.AddRange(head);
        output.Add((byte)(offset & 0xFF));
        output.Add((byte)(offset >> 8));
        WriteLength(output, matchLength - 4);
        WriteToken(output, tail.Length, 0);
        WriteLength(output, tail.Length);
        output.AddRange(tail);
        return output.ToArray();
    }

    private static void WriteToken(List<byte> output, int literals, int match) =>
        output.Add((byte)((Math.Min(literals, 15) << 4) | Math.Min(match, 15)));

    private static void WriteLength(List<byte> output, int length)
    {
        if (length < 15) return;
        var rest = length - 15;
        while (rest >= 255)
        {
            output.Add(255);
            rest -= 255;
        }
        output.Add((byte)rest);
    }
}

internal sealed record SyntheticAsset(string Name, byte[] Script, int ClassId = 49);

internal static class SyntheticUnity
{
    public static byte[] SerializedFile(IReadOnlyList<SyntheticAsset> assets, int version = 22, bool typeTree = false, bool bigEndian = false)
    {
        var classIds = assets.Select(static a => a.ClassId).Distinct().ToList();
        var objects = assets.Select(a => ObjectBytes(a, bigEndian)).ToList();

        var meta = new Writer(bigEndian);
        meta.CString("2020.3.40f1");
        meta.Int32(19);
        meta.Byte(typeTree ? (byte)1 : (byte)0);
        meta.Int32(classIds.Count);
        foreach (var classId in classIds)
        {
            meta.Int32(classId);
            if (version >= 16) meta.Byte(0);
            if (version >= 17) meta.Int16(-1);
            if (classId == 114) meta.Bytes(new byte[16]);
            meta.Bytes(new byte[16]);
            if (typeTree)
            {
                meta.Int32(2);
                meta.Int32(5);
                meta.Bytes(new byte[2 * (version >= 19 ? 32 : 24)]);
                meta.Bytes("abcd\0"u8.ToArray());
                if (version >= 21)
                {
                    meta.Int32(1);
                    meta.Int32(0);
                }
            }
        }

        var headerSize = version >= 22 ? 48 : 20;
        meta.Int32(objects.Count);
        var objectTableStart = headerSize + meta.Length;
        var table = new Writer(bigEndian);
        long offset = 0;
        var offsets = new List<long>();
        foreach (var obj in objects)
        {
            offsets.Add(offset);
            offset += (obj.Length + 7) / 8 * 8;
        }
        for (var i = 0; i < objects.Count; i++)
        {
            var absolute = objectTableStart + table.Length;
            var padding = (4 - absolute % 4) % 4;
            table.Bytes(new byte[padding]);
            table.Int64(i + 1);
            if (version >= 22) table.Int64(offsets[i]);
            else table.UInt32((uint)offsets[i]);
            table.UInt32((uint)objects[i].Length);
            table.Int32(classIds.IndexOf(assets[i].ClassId));
            if (version < 16) table.UInt16((ushort)assets[i].ClassId);
            if (version < 17) table.Int16(-1);
            if (version is 15 or 16) table.Byte(0);
        }
        table.Int32(0);

        var metadataSize = meta.Length + table.Length;
        var dataOffset = (headerSize + metadataSize + 15) / 16 * 16;
        var fileSize = dataOffset + offset;

        var file = new byte[fileSize];
        var header = new Writer(bigEndian: true);
        if (version >= 22)
        {
            header.UInt32(0);
            header.UInt32(0);
            header.UInt32((uint)version);
            header.UInt32(0);
            header.Byte(bigEndian ? (byte)1 : (byte)0);
            header.Bytes([0, 0, 0]);
            header.UInt32((uint)metadataSize);
            header.Int64(fileSize);
            header.Int64(dataOffset);
            header.Int64(0);
        }
        else
        {
            header.UInt32((uint)metadataSize);
            header.UInt32((uint)fileSize);
            header.UInt32((uint)version);
            header.UInt32((uint)dataOffset);
            header.Byte(bigEndian ? (byte)1 : (byte)0);
            header.Bytes([0, 0, 0]);
        }
        header.ToArray().CopyTo(file, 0);
        meta.ToArray().CopyTo(file, headerSize);
        table.ToArray().CopyTo(file, headerSize + meta.Length);
        for (var i = 0; i < objects.Count; i++) objects[i].CopyTo(file, dataOffset + offsets[i]);
        return file;
    }

    private static byte[] ObjectBytes(SyntheticAsset asset, bool bigEndian)
    {
        var writer = new Writer(bigEndian);
        var name = Encoding.UTF8.GetBytes(asset.Name);
        writer.Int32(name.Length);
        writer.Bytes(name);
        writer.Bytes(new byte[(4 - name.Length % 4) % 4]);
        writer.Int32(asset.Script.Length);
        writer.Bytes(asset.Script);
        writer.Bytes(new byte[(4 - asset.Script.Length % 4) % 4]);
        return writer.ToArray();
    }

    public sealed record BundleFile(string Path, byte[] Data);

    public static byte[] Bundle(IReadOnlyList<BundleFile> files, int blockSize = 64, bool infoAtEnd = false,
        bool compressInfo = true, uint extraFlags = 0, Func<int, int>? blockCompression = null, bool padding = true)
    {
        var payload = files.SelectMany(static f => f.Data).ToArray();
        var blocks = new List<(byte[] Stored, int Uncompressed, ushort Flags)>();
        for (var start = 0; start < payload.Length; start += blockSize)
        {
            var chunk = payload.AsSpan(start, Math.Min(blockSize, payload.Length - start)).ToArray();
            var compression = blockCompression?.Invoke(blocks.Count) ?? (blocks.Count % 2 == 0 ? 2 : 0);
            var stored = compression switch
            {
                0 => chunk,
                1 => chunk,
                _ => Lz4Encoder.LiteralsOnly(chunk),
            };
            blocks.Add((stored, chunk.Length, (ushort)compression));
        }

        var info = new Writer(bigEndian: true);
        info.Bytes(new byte[16]);
        info.Int32(blocks.Count);
        foreach (var block in blocks)
        {
            info.UInt32((uint)block.Uncompressed);
            info.UInt32((uint)block.Stored.Length);
            info.UInt16(block.Flags);
        }
        info.Int32(files.Count);
        long nodeOffset = 0;
        foreach (var file in files)
        {
            info.Int64(nodeOffset);
            info.Int64(file.Data.Length);
            info.UInt32(4);
            info.CString(file.Path);
            nodeOffset += file.Data.Length;
        }
        var infoBytes = info.ToArray();
        var storedInfo = compressInfo ? Lz4Encoder.LiteralsOnly(infoBytes) : infoBytes;

        var flags = (compressInfo ? 3u : 0u) | 0x40u | (infoAtEnd ? 0x80u : 0u) | (padding ? 0x200u : 0u) | extraFlags;
        var header = new Writer(bigEndian: true);
        header.CString("UnityFS");
        header.UInt32(8);
        header.CString("5.x.x");
        header.CString("2020.3.40f1");
        var sizePosition = header.Length;
        header.Int64(0);
        header.UInt32((uint)storedInfo.Length);
        header.UInt32((uint)infoBytes.Length);
        header.UInt32(flags);
        header.Bytes(new byte[(16 - header.Length % 16) % 16]);

        var output = new List<byte>(header.ToArray());
        if (!infoAtEnd)
        {
            output.AddRange(storedInfo);
            if (padding) output.AddRange(new byte[(16 - output.Count % 16) % 16]);
        }
        foreach (var block in blocks) output.AddRange(block.Stored);
        if (infoAtEnd) output.AddRange(storedInfo);

        var bytes = output.ToArray();
        BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(sizePosition), bytes.Length);
        return bytes;
    }

    public static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private sealed class Writer(bool bigEndian)
    {
        private readonly List<byte> _bytes = [];

        public int Length => _bytes.Count;

        public byte[] ToArray() => _bytes.ToArray();

        public void Byte(byte value) => _bytes.Add(value);

        public void Bytes(byte[] values) => _bytes.AddRange(values);

        public void CString(string value)
        {
            _bytes.AddRange(Encoding.UTF8.GetBytes(value));
            _bytes.Add(0);
        }

        public void Int16(short value)
        {
            Span<byte> buffer = stackalloc byte[2];
            if (bigEndian) BinaryPrimitives.WriteInt16BigEndian(buffer, value);
            else BinaryPrimitives.WriteInt16LittleEndian(buffer, value);
            _bytes.AddRange(buffer.ToArray());
        }

        public void UInt16(ushort value)
        {
            Span<byte> buffer = stackalloc byte[2];
            if (bigEndian) BinaryPrimitives.WriteUInt16BigEndian(buffer, value);
            else BinaryPrimitives.WriteUInt16LittleEndian(buffer, value);
            _bytes.AddRange(buffer.ToArray());
        }

        public void Int32(int value)
        {
            Span<byte> buffer = stackalloc byte[4];
            if (bigEndian) BinaryPrimitives.WriteInt32BigEndian(buffer, value);
            else BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
            _bytes.AddRange(buffer.ToArray());
        }

        public void UInt32(uint value)
        {
            Span<byte> buffer = stackalloc byte[4];
            if (bigEndian) BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
            else BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
            _bytes.AddRange(buffer.ToArray());
        }

        public void Int64(long value)
        {
            Span<byte> buffer = stackalloc byte[8];
            if (bigEndian) BinaryPrimitives.WriteInt64BigEndian(buffer, value);
            else BinaryPrimitives.WriteInt64LittleEndian(buffer, value);
            _bytes.AddRange(buffer.ToArray());
        }
    }
}

internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gto-corpustool-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public string WriteFile(string relative, byte[] content)
    {
        var full = Combine(relative.Split('/'));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
        return full;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
