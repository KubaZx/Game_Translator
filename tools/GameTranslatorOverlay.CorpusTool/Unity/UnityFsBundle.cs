using GameTranslatorOverlay.CorpusTool.Safety;

namespace GameTranslatorOverlay.CorpusTool.Unity;

public sealed record BundleNode(string Path, long Offset, long Size, uint Flags);

public sealed record BundleBlock(uint UncompressedSize, uint CompressedSize, ushort Flags)
{
    public int Compression => Flags & 0x3F;
}

public sealed class UnityFsBundle : IDisposable
{
    public const uint CompressionMask = 0x3F;
    public const uint BlocksAndDirectoryCombined = 0x40;
    public const uint BlocksInfoAtTheEnd = 0x80;
    public const uint OldWebPluginCompatibility = 0x100;
    public const uint BlockInfoNeedPaddingAtStart = 0x200;
    public const uint KnownFlags = 0x3FF;
    public const long MaxNodeSize = 1L << 30;

    private static readonly byte[] Signature = "UnityFS\0"u8.ToArray();
    private static readonly byte[] ChinaEncryptionMarker = "#$unity3dchina!@"u8.ToArray();

    private readonly Stream _stream;
    private readonly long[] _blockUncompressedOffsets;
    private readonly long[] _blockCompressedOffsets;

    private UnityFsBundle(Stream stream, uint formatVersion, string unityVersion, string unityRevision, uint flags,
        IReadOnlyList<BundleBlock> blocks, IReadOnlyList<BundleNode> nodes, long dataStart)
    {
        _stream = stream;
        FormatVersion = formatVersion;
        UnityVersion = unityVersion;
        UnityRevision = unityRevision;
        Flags = flags;
        Blocks = blocks;
        Nodes = nodes;
        _blockUncompressedOffsets = new long[blocks.Count];
        _blockCompressedOffsets = new long[blocks.Count];
        long uncompressed = 0;
        var compressed = dataStart;
        for (var i = 0; i < blocks.Count; i++)
        {
            _blockUncompressedOffsets[i] = uncompressed;
            _blockCompressedOffsets[i] = compressed;
            uncompressed += blocks[i].UncompressedSize;
            compressed += blocks[i].CompressedSize;
        }
        UncompressedLength = uncompressed;
    }

    public uint FormatVersion { get; }
    public string UnityVersion { get; }
    public string UnityRevision { get; }
    public uint Flags { get; }
    public IReadOnlyList<BundleBlock> Blocks { get; }
    public IReadOnlyList<BundleNode> Nodes { get; }
    public long UncompressedLength { get; }

    public static bool HasSignature(ReadOnlySpan<byte> head) => head.StartsWith(Signature);

    public static UnityFsBundle Open(string path) => Open(ReadOnlyGameFile.Open(path), ownsStream: true);

    public static UnityFsBundle Open(Stream stream, bool ownsStream = false)
    {
        try
        {
            return Read(stream);
        }
        catch
        {
            if (ownsStream) stream.Dispose();
            throw;
        }
    }

    private static UnityFsBundle Read(Stream stream)
    {
        var headLength = (int)Math.Min(stream.Length, 4096);
        var head = new byte[headLength];
        stream.Position = 0;
        stream.ReadExactly(head);
        if (!HasSignature(head)) throw new InvalidDataException("To nie jest kontener UnityFS.");
        if (head.AsSpan().IndexOf(ChinaEncryptionMarker) >= 0)
        {
            throw new ProtectedContainerException("Kontener UnityFS jest zaszyfrowany (UnityCN) — narzędzie nie odszyfrowuje danych gry.");
        }

        var cursor = new BinaryCursor(head, bigEndian: true);
        cursor.ReadCString();
        var formatVersion = cursor.ReadUInt32();
        var unityVersion = cursor.ReadCString();
        var unityRevision = cursor.ReadCString();
        if (formatVersion is < 6 or > 8)
        {
            throw new NotSupportedException($"Nieobsługiwana wersja formatu UnityFS: {formatVersion} (obsługiwane 6–8).");
        }
        var totalSize = cursor.ReadInt64();
        var compressedInfoSize = cursor.ReadUInt32();
        var uncompressedInfoSize = cursor.ReadUInt32();
        var flags = cursor.ReadUInt32();
        if ((flags & ~KnownFlags) != 0)
        {
            throw new ProtectedContainerException(
                $"Kontener UnityFS ma nieznane flagi 0x{flags:X} (możliwe szyfrowanie) — narzędzie nie odszyfrowuje danych gry.");
        }
        if (totalSize > stream.Length || compressedInfoSize > stream.Length || uncompressedInfoSize > 64 * 1024 * 1024)
        {
            throw new InvalidDataException("Nagłówek UnityFS ma niespójne rozmiary.");
        }
        if (formatVersion >= 7) cursor.Align(16);

        long infoPosition;
        long afterInfo;
        if ((flags & BlocksInfoAtTheEnd) != 0)
        {
            infoPosition = stream.Length - compressedInfoSize;
            afterInfo = cursor.Position;
        }
        else
        {
            infoPosition = cursor.Position;
            afterInfo = cursor.Position + compressedInfoSize;
        }
        if ((flags & BlockInfoNeedPaddingAtStart) != 0) afterInfo = (afterInfo + 15) / 16 * 16;

        var compressedInfo = new byte[compressedInfoSize];
        stream.Position = infoPosition;
        stream.ReadExactly(compressedInfo);
        var info = Decompress(compressedInfo, (int)uncompressedInfoSize, (int)(flags & CompressionMask), "katalog bloków");

        var infoCursor = new BinaryCursor(info, bigEndian: true);
        infoCursor.Skip(16);
        var blockCount = infoCursor.ReadInt32();
        if (blockCount < 0 || blockCount > info.Length / 10) throw new InvalidDataException("Niespójna liczba bloków UnityFS.");
        var blocks = new List<BundleBlock>(blockCount);
        long compressedTotal = 0;
        for (var i = 0; i < blockCount; i++)
        {
            var block = new BundleBlock(infoCursor.ReadUInt32(), infoCursor.ReadUInt32(), infoCursor.ReadUInt16());
            compressedTotal += block.CompressedSize;
            blocks.Add(block);
        }
        if (afterInfo + compressedTotal > stream.Length) throw new InvalidDataException("Bloki UnityFS wychodzą poza plik.");

        var nodeCount = infoCursor.ReadInt32();
        if (nodeCount < 0 || nodeCount > info.Length / 20) throw new InvalidDataException("Niespójna liczba plików w kontenerze UnityFS.");
        var nodes = new List<BundleNode>(nodeCount);
        for (var i = 0; i < nodeCount; i++)
        {
            var offset = infoCursor.ReadInt64();
            var size = infoCursor.ReadInt64();
            var nodeFlags = infoCursor.ReadUInt32();
            var name = infoCursor.ReadCString();
            nodes.Add(new BundleNode(name, offset, size, nodeFlags));
        }

        var bundle = new UnityFsBundle(stream, formatVersion, unityVersion, unityRevision, flags, blocks, nodes, afterInfo);
        foreach (var node in nodes)
        {
            if (node.Offset < 0 || node.Size < 0 || node.Offset + node.Size > bundle.UncompressedLength)
            {
                bundle.Dispose();
                throw new InvalidDataException($"Plik „{node.Path}” wychodzi poza dane kontenera UnityFS.");
            }
        }
        return bundle;
    }

    public BundleNode? FindNode(string path) =>
        Nodes.FirstOrDefault(n => n.Path.Equals(path, StringComparison.Ordinal))
        ?? Nodes.FirstOrDefault(n => n.Path.Equals(path, StringComparison.OrdinalIgnoreCase));

    public byte[] ReadNode(BundleNode node)
    {
        if (node.Size > MaxNodeSize) throw new NotSupportedException($"Plik „{node.Path}” jest za duży ({node.Size} B).");
        var result = new byte[node.Size];
        var nodeEnd = node.Offset + node.Size;
        byte[]? compressedBuffer = null;
        byte[]? blockBuffer = null;

        for (var i = 0; i < Blocks.Count; i++)
        {
            var blockStart = _blockUncompressedOffsets[i];
            var block = Blocks[i];
            var blockEnd = blockStart + block.UncompressedSize;
            if (blockEnd <= node.Offset) continue;
            if (blockStart >= nodeEnd) break;

            if (compressedBuffer is null || compressedBuffer.Length < block.CompressedSize) compressedBuffer = new byte[block.CompressedSize];
            _stream.Position = _blockCompressedOffsets[i];
            _stream.ReadExactly(compressedBuffer, 0, (int)block.CompressedSize);

            ReadOnlySpan<byte> decoded;
            if (block.Compression == 0)
            {
                if (block.CompressedSize != block.UncompressedSize) throw new InvalidDataException("Blok bez kompresji ma niespójny rozmiar.");
                decoded = compressedBuffer.AsSpan(0, (int)block.CompressedSize);
            }
            else
            {
                if (blockBuffer is null || blockBuffer.Length < block.UncompressedSize) blockBuffer = new byte[block.UncompressedSize];
                DecodeInto(compressedBuffer.AsSpan(0, (int)block.CompressedSize), blockBuffer.AsSpan(0, (int)block.UncompressedSize), block.Compression);
                decoded = blockBuffer.AsSpan(0, (int)block.UncompressedSize);
            }

            var copyStart = Math.Max(blockStart, node.Offset);
            var copyEnd = Math.Min(blockEnd, nodeEnd);
            decoded.Slice((int)(copyStart - blockStart), (int)(copyEnd - copyStart))
                .CopyTo(result.AsSpan((int)(copyStart - node.Offset)));
        }
        return result;
    }

    private static byte[] Decompress(byte[] source, int uncompressedSize, int compression, string what)
    {
        if (compression == 0)
        {
            if (source.Length != uncompressedSize) throw new InvalidDataException($"Nieskompresowany {what} ma niespójny rozmiar.");
            return source;
        }
        var target = new byte[uncompressedSize];
        DecodeInto(source, target, compression);
        return target;
    }

    private static void DecodeInto(ReadOnlySpan<byte> source, Span<byte> target, int compression)
    {
        switch (compression)
        {
            case 2 or 3:
                int written;
                try
                {
                    written = Lz4Block.Decode(source, target);
                }
                catch (InvalidDataException ex)
                {
                    throw new InvalidDataException("Nie da się rozpakować danych (kontener uszkodzony albo zaszyfrowany).", ex);
                }
                if (written != target.Length) throw new InvalidDataException("Rozpakowany blok ma inny rozmiar niż zapisany w katalogu.");
                break;
            case 1:
                throw new NotSupportedException("Kompresja LZMA w kontenerze UnityFS nie jest obsługiwana.");
            default:
                throw new NotSupportedException($"Nieznany rodzaj kompresji bloku: {compression}.");
        }
    }

    public void Dispose() => _stream.Dispose();
}
