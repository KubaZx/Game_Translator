using GameTranslatorOverlay.CorpusTool.Safety;

namespace GameTranslatorOverlay.CorpusTool.Unity;

public sealed record UnityReadResult(
    string ContainerKind,
    string? UnityVersion,
    int SerializedVersion,
    int ObjectCount,
    IReadOnlyList<TextAssetData> TextAssets);

public static class UnityTextAssetReader
{
    public const string FormatId = "unity-textasset";

    public static UnityReadResult Read(string containerPath, string? innerFile)
    {
        using var stream = ReadOnlyGameFile.Open(containerPath);
        var head = new byte[Math.Min(stream.Length, 64)];
        stream.ReadExactly(head);
        stream.Position = 0;

        if (UnityFsBundle.HasSignature(head))
        {
            using var bundle = UnityFsBundle.Open(stream);
            if (string.IsNullOrWhiteSpace(innerFile))
            {
                throw new InvalidDataException("Kontener UnityFS wymaga wskazania pliku w środku (pole „corpus.file”).");
            }
            var node = bundle.FindNode(innerFile)
                ?? throw new FileNotFoundException($"W kontenerze nie ma pliku „{innerFile}”.");
            var data = bundle.ReadNode(node);
            var file = SerializedFile.Parse(data);
            return new UnityReadResult("unityfs", bundle.UnityRevision, file.Version, file.Objects.Count, file.ReadTextAssets().ToList());
        }

        if (SerializedFile.LooksLikeSerializedFile(head))
        {
            if (stream.Length > UnityFsBundle.MaxNodeSize) throw new NotSupportedException("Plik serializowany jest za duży.");
            var data = new byte[stream.Length];
            stream.ReadExactly(data);
            var file = SerializedFile.Parse(data);
            return new UnityReadResult("serialized", file.UnityVersion, file.Version, file.Objects.Count, file.ReadTextAssets().ToList());
        }

        throw new InvalidDataException("Plik nie jest kontenerem UnityFS ani plikiem serializowanym Unity.");
    }
}
