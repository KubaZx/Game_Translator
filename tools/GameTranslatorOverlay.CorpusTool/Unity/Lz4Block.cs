namespace GameTranslatorOverlay.CorpusTool.Unity;

public static class Lz4Block
{
    public static int Decode(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        var input = 0;
        var output = 0;
        while (input < source.Length)
        {
            var token = source[input++];
            var literalLength = token >> 4;
            if (literalLength == 15)
            {
                byte extra;
                do
                {
                    if (input >= source.Length) throw Malformed("długość literałów wychodzi poza blok");
                    extra = source[input++];
                    literalLength += extra;
                } while (extra == 255);
            }

            if (literalLength > source.Length - input) throw Malformed("literały wychodzą poza blok");
            if (literalLength > destination.Length - output) throw Malformed("literały nie mieszczą się w buforze");
            source.Slice(input, literalLength).CopyTo(destination[output..]);
            input += literalLength;
            output += literalLength;

            if (input >= source.Length) break;

            if (source.Length - input < 2) throw Malformed("ucięte przesunięcie dopasowania");
            var offset = source[input] | (source[input + 1] << 8);
            input += 2;
            if (offset == 0 || offset > output) throw Malformed("przesunięcie dopasowania poza zdekodowanymi danymi");

            var matchLength = token & 15;
            if (matchLength == 15)
            {
                byte extra;
                do
                {
                    if (input >= source.Length) throw Malformed("długość dopasowania wychodzi poza blok");
                    extra = source[input++];
                    matchLength += extra;
                } while (extra == 255);
            }
            matchLength += 4;
            if (matchLength > destination.Length - output) throw Malformed("dopasowanie nie mieści się w buforze");

            var from = output - offset;
            if (offset >= matchLength)
            {
                destination.Slice(from, matchLength).CopyTo(destination[output..]);
                output += matchLength;
            }
            else
            {
                for (var k = 0; k < matchLength; k++) destination[output++] = destination[from + k];
            }
        }
        return output;
    }

    private static InvalidDataException Malformed(string reason) =>
        new($"Uszkodzony blok LZ4: {reason}.");
}
