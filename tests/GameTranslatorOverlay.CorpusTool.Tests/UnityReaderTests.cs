using GameTranslatorOverlay.CorpusTool.Safety;
using GameTranslatorOverlay.CorpusTool.Unity;

namespace GameTranslatorOverlay.CorpusTool.Tests;

public class Lz4BlockTests
{
    [Fact]
    public void Dekoduje_same_literaly_takze_dluzsze_niz_15_bajtow()
    {
        var data = Enumerable.Range(0, 300).Select(static i => (byte)(i * 7)).ToArray();
        var target = new byte[data.Length];

        var written = Lz4Block.Decode(Lz4Encoder.LiteralsOnly(data), target);

        Assert.Equal(data.Length, written);
        Assert.Equal(data, target);
    }

    [Fact]
    public void Dekoduje_nakladajace_sie_dopasowanie()
    {
        var encoded = Lz4Encoder.WithRepeat("abc"u8.ToArray(), offset: 3, matchLength: 40, "XYZ!!"u8.ToArray());
        var target = new byte[48];

        var written = Lz4Block.Decode(encoded, target);

        Assert.Equal(48, written);
        Assert.Equal(string.Concat(Enumerable.Repeat("abc", 15))[..43] + "XYZ!!", System.Text.Encoding.ASCII.GetString(target));
    }

    [Fact]
    public void Dekoduje_dopasowanie_bez_nakladania()
    {
        var encoded = Lz4Encoder.WithRepeat("0123456789"u8.ToArray(), offset: 10, matchLength: 6, "end"u8.ToArray());
        var target = new byte[19];

        Lz4Block.Decode(encoded, target);

        Assert.Equal("0123456789012345end", System.Text.Encoding.ASCII.GetString(target));
    }

    [Fact]
    public void Odrzuca_przesuniecie_poza_dane_i_przepelnienie_bufora()
    {
        var badOffset = Lz4Encoder.WithRepeat("ab"u8.ToArray(), offset: 9, matchLength: 4, "c"u8.ToArray());
        Assert.Throws<InvalidDataException>(() => Lz4Block.Decode(badOffset, new byte[16]));
        Assert.Throws<InvalidDataException>(() => Lz4Block.Decode(Lz4Encoder.LiteralsOnly(new byte[20]), new byte[10]));
        Assert.Throws<InvalidDataException>(() => Lz4Block.Decode([0xF0], new byte[64]));
        Assert.Throws<InvalidDataException>(() => Lz4Block.Decode([0x10, 0x41, 0x01], new byte[64]));
    }
}

public class UnityFsBundleTests
{
    private static readonly SyntheticAsset[] Assets =
    [
        new("Strings_Main", SyntheticUnity.Utf8("Key,Value-En\nhello,Hello there\n")),
        new("Notes", SyntheticUnity.Utf8(new string('x', 150))),
        new("Mesh", [1, 2, 3, 4], ClassId: 43),
    ];

    private static byte[] Serialized() => SyntheticUnity.SerializedFile(Assets);

    [Theory]
    [InlineData(false, true, 64)]
    [InlineData(true, true, 50)]
    [InlineData(false, false, 1000)]
    public void Czyta_plik_z_kontenera_przez_granice_blokow(bool infoAtEnd, bool compressInfo, int blockSize)
    {
        var serialized = Serialized();
        var other = Enumerable.Range(0, 77).Select(static i => (byte)i).ToArray();
        var bundle = SyntheticUnity.Bundle(
            [new SyntheticUnity.BundleFile("globalgamemanagers", other), new SyntheticUnity.BundleFile("resources.assets", serialized)],
            blockSize, infoAtEnd, compressInfo);

        using var reader = UnityFsBundle.Open(new MemoryStream(bundle));
        var node = reader.FindNode("RESOURCES.assets");

        Assert.Equal(8u, reader.FormatVersion);
        Assert.Equal("2020.3.40f1", reader.UnityRevision);
        Assert.Equal(2, reader.Nodes.Count);
        Assert.NotNull(node);
        Assert.Equal(serialized, reader.ReadNode(node));
        Assert.Equal(other, reader.ReadNode(reader.FindNode("globalgamemanagers")!));
        Assert.Null(reader.FindNode("missing"));
    }

    [Fact]
    public void Nieznane_flagi_traktuje_jak_szyfrowanie_i_odmawia()
    {
        var bundle = SyntheticUnity.Bundle([new SyntheticUnity.BundleFile("resources.assets", Serialized())], extraFlags: 0x400);

        Assert.Throws<ProtectedContainerException>(() => UnityFsBundle.Open(new MemoryStream(bundle)));
    }

    [Fact]
    public void Znacznik_szyfrowania_unitycn_daje_odmowe()
    {
        var bundle = SyntheticUnity.Bundle([new SyntheticUnity.BundleFile("resources.assets", Serialized())]);
        var marked = bundle.Take(64).Concat("#$unity3dchina!@"u8.ToArray()).Concat(bundle.Skip(64)).ToArray();

        Assert.Throws<ProtectedContainerException>(() => UnityFsBundle.Open(new MemoryStream(marked)));
    }

    [Fact]
    public void Blok_lzma_jest_nieobslugiwany()
    {
        var bundle = SyntheticUnity.Bundle([new SyntheticUnity.BundleFile("resources.assets", Serialized())], blockCompression: static _ => 1);

        using var reader = UnityFsBundle.Open(new MemoryStream(bundle));

        Assert.Throws<NotSupportedException>(() => reader.ReadNode(reader.Nodes[0]));
    }

    [Fact]
    public void Uszkodzony_katalog_blokow_daje_blad_danych()
    {
        var bundle = SyntheticUnity.Bundle([new SyntheticUnity.BundleFile("resources.assets", Serialized())]);
        for (var i = 64; i < 80; i++) bundle[i] = 0xFF;

        Assert.ThrowsAny<InvalidDataException>(() => UnityFsBundle.Open(new MemoryStream(bundle)));
    }

    [Fact]
    public void Plik_bez_sygnatury_nie_jest_kontenerem()
    {
        Assert.Throws<InvalidDataException>(() => UnityFsBundle.Open(new MemoryStream(new byte[128])));
        Assert.False(UnityFsBundle.HasSignature("UnityWeb\0"u8));
    }
}

public class SerializedFileTests
{
    [Theory]
    [InlineData(22, false, false)]
    [InlineData(21, true, false)]
    [InlineData(17, true, true)]
    [InlineData(15, false, false)]
    public void Czyta_textassety_w_roznych_wersjach(int version, bool typeTree, bool bigEndian)
    {
        var assets = new[]
        {
            new SyntheticAsset("First", SyntheticUnity.Utf8("alpha")),
            new SyntheticAsset("Second asset", SyntheticUnity.Utf8("beta gamma")),
            new SyntheticAsset("Script", [9, 9], ClassId: 114),
        };
        if (version < 16) assets = assets[..2];

        var file = SerializedFile.Parse(SyntheticUnity.SerializedFile(assets, version, typeTree, bigEndian));
        var textAssets = file.ReadTextAssets().ToList();

        Assert.Equal(version, file.Version);
        Assert.Equal("2020.3.40f1", file.UnityVersion);
        Assert.Equal(["First", "Second asset"], textAssets.Select(static t => t.Name));
        Assert.Equal("beta gamma", System.Text.Encoding.UTF8.GetString(textAssets[1].Script));
        Assert.Equal(bigEndian, file.BigEndian);
    }

    [Fact]
    public void Odrzuca_nieobslugiwane_wersje_i_obcinane_dane()
    {
        var old = SyntheticUnity.SerializedFile([new SyntheticAsset("A", [1])], version: 22);
        old[11] = 9;

        Assert.Throws<NotSupportedException>(() => SerializedFile.Parse(old));
        var good = SyntheticUnity.SerializedFile([new SyntheticAsset("A", SyntheticUnity.Utf8("text"))]);
        Assert.ThrowsAny<InvalidDataException>(() => SerializedFile.Parse(good[..60]));
    }

    [Fact]
    public void Rozpoznaje_naglowek_pliku_serializowanego()
    {
        var file = SyntheticUnity.SerializedFile([new SyntheticAsset("A", [1])]);

        Assert.True(SerializedFile.LooksLikeSerializedFile(file));
        Assert.False(SerializedFile.LooksLikeSerializedFile(new byte[32]));
        Assert.False(SerializedFile.LooksLikeSerializedFile(new byte[8]));
    }

    [Fact]
    public void Czytnik_obsluguje_kontener_i_luzny_plik_tylko_do_odczytu()
    {
        using var temp = new TempDirectory();
        var serialized = SyntheticUnity.SerializedFile([new SyntheticAsset("Strings", SyntheticUnity.Utf8("a,b"))]);
        var bundlePath = temp.WriteFile("Game_Data/data.unity3d", SyntheticUnity.Bundle([new SyntheticUnity.BundleFile("resources.assets", serialized)]));
        var loosePath = temp.WriteFile("Game_Data/resources.assets", serialized);

        var fromBundle = UnityTextAssetReader.Read(bundlePath, "resources.assets");
        var fromLoose = UnityTextAssetReader.Read(loosePath, null);

        Assert.Equal("unityfs", fromBundle.ContainerKind);
        Assert.Equal("serialized", fromLoose.ContainerKind);
        Assert.Equal("Strings", Assert.Single(fromBundle.TextAssets).Name);
        Assert.Equal("Strings", Assert.Single(fromLoose.TextAssets).Name);
        Assert.Throws<InvalidDataException>(() => UnityTextAssetReader.Read(bundlePath, null));
        Assert.Throws<FileNotFoundException>(() => UnityTextAssetReader.Read(bundlePath, "missing.assets"));
        var other = temp.WriteFile("other.bin", new byte[100]);
        Assert.Throws<InvalidDataException>(() => UnityTextAssetReader.Read(other, null));
    }
}
