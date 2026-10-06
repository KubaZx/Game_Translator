namespace GameTranslatorOverlay.CorpusTool.Safety;

public sealed class ProtectedContainerException(string message) : Exception(message);

public sealed class RefusedException(string message) : Exception(message);

public static class ReadOnlyGameFile
{
    public const FileShare Sharing = FileShare.ReadWrite | FileShare.Delete;

    public static FileStream Open(string path) =>
        new(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = Sharing,
            Options = FileOptions.RandomAccess,
            BufferSize = 1 << 16,
        });

    public static byte[] ReadHead(string path, int count)
    {
        using var stream = Open(path);
        var buffer = new byte[(int)Math.Min(count, stream.Length)];
        stream.ReadExactly(buffer);
        return buffer;
    }

    public static byte[] ReadTail(string path, int count)
    {
        using var stream = Open(path);
        var length = (int)Math.Min(count, stream.Length);
        var buffer = new byte[length];
        stream.Position = stream.Length - length;
        stream.ReadExactly(buffer);
        return buffer;
    }
}
