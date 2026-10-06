namespace GameTranslatorOverlay.CorpusTool.Unity;

public sealed record SerializedObject(long PathId, long ByteStart, uint ByteSize, int ClassId);

public sealed record TextAssetData(string Name, byte[] Script);

public sealed class SerializedFile
{
    public const int TextAssetClassId = 49;
    public const int MinVersion = 14;
    public const int MaxVersion = 23;

    private readonly byte[] _data;

    private SerializedFile(byte[] data, int version, string unityVersion, bool bigEndian, IReadOnlyList<SerializedObject> objects)
    {
        _data = data;
        Version = version;
        UnityVersion = unityVersion;
        BigEndian = bigEndian;
        Objects = objects;
    }

    public int Version { get; }
    public string UnityVersion { get; }
    public bool BigEndian { get; }
    public IReadOnlyList<SerializedObject> Objects { get; }

    public static bool LooksLikeSerializedFile(ReadOnlySpan<byte> head)
    {
        if (head.Length < 20) return false;
        var version = (head[8] << 24) | (head[9] << 16) | (head[10] << 8) | head[11];
        return version is >= MinVersion and <= MaxVersion && head[16] is 0 or 1;
    }

    public static SerializedFile Parse(byte[] data)
    {
        var cursor = new BinaryCursor(data, bigEndian: true);
        cursor.ReadUInt32();
        long fileSize = cursor.ReadUInt32();
        var version = (int)cursor.ReadUInt32();
        long dataOffset = cursor.ReadUInt32();
        if (version is < MinVersion or > MaxVersion)
        {
            throw new NotSupportedException($"Nieobsługiwana wersja pliku serializowanego Unity: {version} (obsługiwane {MinVersion}–{MaxVersion}).");
        }
        var endianness = cursor.ReadByte();
        cursor.Skip(3);
        if (version >= 22)
        {
            cursor.ReadUInt32();
            fileSize = cursor.ReadInt64();
            dataOffset = cursor.ReadInt64();
            cursor.ReadInt64();
        }
        if (fileSize > data.Length || dataOffset < 0 || dataOffset > data.Length)
        {
            throw new InvalidDataException("Nagłówek pliku serializowanego Unity ma niespójne rozmiary.");
        }

        cursor.BigEndian = endianness != 0;
        var unityVersion = cursor.ReadCString();
        cursor.ReadInt32();
        var enableTypeTree = cursor.ReadBool();

        var typeCount = cursor.ReadInt32();
        if (typeCount < 0 || typeCount > 100_000) throw new InvalidDataException("Niespójna liczba typów w pliku serializowanym.");
        var typeClassIds = new int[typeCount];
        for (var i = 0; i < typeCount; i++)
        {
            typeClassIds[i] = ReadSerializedType(cursor, version, enableTypeTree);
        }

        var objectCount = cursor.ReadInt32();
        if (objectCount < 0 || objectCount > data.Length / 12) throw new InvalidDataException("Niespójna liczba obiektów w pliku serializowanym.");
        var objects = new List<SerializedObject>(objectCount);
        for (var i = 0; i < objectCount; i++)
        {
            cursor.Align(4);
            var pathId = cursor.ReadInt64();
            long byteStart = version >= 22 ? cursor.ReadInt64() : cursor.ReadUInt32();
            byteStart += dataOffset;
            var byteSize = cursor.ReadUInt32();
            var typeId = cursor.ReadInt32();
            int classId;
            if (version < 16)
            {
                classId = cursor.ReadUInt16();
            }
            else
            {
                if (typeId < 0 || typeId >= typeClassIds.Length) throw new InvalidDataException("Obiekt wskazuje nieistniejący typ.");
                classId = typeClassIds[typeId];
            }
            if (version < 17) cursor.ReadInt16();
            if (version is 15 or 16) cursor.ReadByte();
            if (byteStart < 0 || byteStart + byteSize > data.Length) throw new InvalidDataException("Obiekt wychodzi poza plik serializowany.");
            objects.Add(new SerializedObject(pathId, byteStart, byteSize, classId));
        }

        return new SerializedFile(data, version, unityVersion, cursor.BigEndian, objects);
    }

    private static int ReadSerializedType(BinaryCursor cursor, int version, bool enableTypeTree)
    {
        var classId = cursor.ReadInt32();
        if (version >= 16) cursor.ReadBool();
        if (version >= 17) cursor.ReadInt16();
        if ((version < 16 && classId < 0) || (version >= 16 && classId == 114)) cursor.Skip(16);
        cursor.Skip(16);
        if (enableTypeTree)
        {
            var nodeCount = cursor.ReadInt32();
            var stringBufferSize = cursor.ReadInt32();
            if (nodeCount < 0 || stringBufferSize < 0) throw new InvalidDataException("Niespójne drzewo typów.");
            cursor.Skip(checked(nodeCount * (version >= 19 ? 32 : 24)));
            cursor.Skip(stringBufferSize);
            if (version >= 21)
            {
                var dependencies = cursor.ReadInt32();
                if (dependencies < 0) throw new InvalidDataException("Niespójna lista zależności typu.");
                cursor.Skip(checked(dependencies * 4));
            }
        }
        return classId;
    }

    public IEnumerable<TextAssetData> ReadTextAssets()
    {
        foreach (var obj in Objects)
        {
            if (obj.ClassId != TextAssetClassId) continue;
            yield return ReadTextAsset(obj);
        }
    }

    public TextAssetData ReadTextAsset(SerializedObject obj)
    {
        var cursor = new BinaryCursor(_data, (int)obj.ByteStart, (int)obj.ByteSize, BigEndian);
        var nameLength = cursor.ReadInt32();
        if (nameLength < 0 || nameLength > cursor.Remaining) throw new InvalidDataException("Niespójna długość nazwy TextAssetu.");
        var name = System.Text.Encoding.UTF8.GetString(cursor.ReadSpan(nameLength));
        cursor.Align(4);
        var scriptLength = cursor.ReadInt32();
        if (scriptLength < 0 || scriptLength > cursor.Remaining) throw new InvalidDataException($"Niespójna długość treści TextAssetu „{name}”.");
        return new TextAssetData(name, cursor.ReadSpan(scriptLength).ToArray());
    }
}
