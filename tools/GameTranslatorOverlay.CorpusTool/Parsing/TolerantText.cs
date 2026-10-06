using System.Text;

namespace GameTranslatorOverlay.CorpusTool.Parsing;

public readonly record struct DecodedText(string Text, int FallbackBytes);

public static class TolerantText
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly char[] Windows1252High =
    [
        '€', '\u0081', '‚', 'ƒ', '„', '…', '†', '‡',
        'ˆ', '‰', 'Š', '‹', 'Œ', '\u008D', 'Ž', '\u008F',
        '\u0090', '‘', '’', '“', '”', '•', '–', '—',
        '˜', '™', 'š', '›', 'œ', '\u009D', 'ž', 'Ÿ',
    ];

    public static DecodedText Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) bytes = bytes[3..];
        try
        {
            return new DecodedText(StrictUtf8.GetString(bytes), 0);
        }
        catch (DecoderFallbackException)
        {
            return DecodeMixed(bytes);
        }
    }

    private static DecodedText DecodeMixed(ReadOnlySpan<byte> bytes)
    {
        var builder = new StringBuilder(bytes.Length);
        var fallback = 0;
        var i = 0;
        while (i < bytes.Length)
        {
            var b = bytes[i];
            if (b < 0x80)
            {
                builder.Append((char)b);
                i++;
                continue;
            }
            var length = SequenceLength(bytes[i..]);
            if (length > 0)
            {
                builder.Append(Encoding.UTF8.GetString(bytes.Slice(i, length)));
                i += length;
                continue;
            }
            builder.Append(b is >= 0x80 and <= 0x9F ? Windows1252High[b - 0x80] : (char)b);
            fallback++;
            i++;
        }
        return new DecodedText(builder.ToString(), fallback);
    }

    private static int SequenceLength(ReadOnlySpan<byte> bytes)
    {
        var lead = bytes[0];
        int length;
        int minimum;
        if (lead is >= 0xC2 and <= 0xDF)
        {
            length = 2;
            minimum = 0x80;
        }
        else if (lead is >= 0xE0 and <= 0xEF)
        {
            length = 3;
            minimum = 0x800;
        }
        else if (lead is >= 0xF0 and <= 0xF4)
        {
            length = 4;
            minimum = 0x10000;
        }
        else
        {
            return 0;
        }
        if (bytes.Length < length) return 0;
        var value = lead & (0xFF >> (length + 1));
        for (var k = 1; k < length; k++)
        {
            if ((bytes[k] & 0xC0) != 0x80) return 0;
            value = (value << 6) | (bytes[k] & 0x3F);
        }
        if (value < minimum || value > 0x10FFFF || value is >= 0xD800 and <= 0xDFFF) return 0;
        return length;
    }
}
