using System.Text;

namespace GameTranslatorOverlay.Infrastructure.Providers;

public sealed class LlmTranslationStreamParser(int expectedCount, Action<int, string> onTranslation)
{
    private enum State
    {
        Seek,
        KeyString,
        AfterKey,
        AfterColon,
        ArrayValue,
        ArrayAfterValue,
        Element,
        Think,
        Done,
    }

    private static readonly string[] TranslationKeys = ["translations", "Translations", "translation", "result"];
    private const string ThinkOpen = "<think>";
    private const string ThinkClose = "</think>";

    private readonly StringBuilder _value = new();
    private readonly StringBuilder _tag = new();
    private State _state = State.Seek;
    private bool _escape;
    private int _unicodeDigits = -1;
    private int _unicodeValue;
    private int _next;

    public int Emitted => _next;

    public bool IsStopped => _state == State.Done;

    public void Stop() => _state = State.Done;

    public void Append(string? delta)
    {
        if (string.IsNullOrEmpty(delta)) return;
        foreach (var c in delta)
        {
            if (_state == State.Done) return;
            Feed(c);
        }
    }

    private void Feed(char c)
    {
        while (true)
        {
            switch (_state)
            {
                case State.Seek:
                    if (c == '"')
                    {
                        BeginString();
                        _tag.Clear();
                        _state = State.KeyString;
                    }
                    else if (WatchTag(c, ThinkOpen))
                    {
                        _state = State.Think;
                    }
                    return;
                case State.Think:
                    if (WatchTag(c, ThinkClose)) _state = State.Seek;
                    return;
                case State.KeyString:
                    switch (ReadStringChar(c))
                    {
                        case StringStep.Closed:
                            _state = Array.IndexOf(TranslationKeys, _value.ToString()) >= 0 ? State.AfterKey : State.Seek;
                            break;
                        case StringStep.Invalid:
                            _state = State.Seek;
                            break;
                    }
                    return;
                case State.AfterKey:
                    if (IsWhitespace(c)) return;
                    if (c == ':')
                    {
                        _state = State.AfterColon;
                        return;
                    }
                    _state = State.Seek;
                    continue;
                case State.AfterColon:
                    if (IsWhitespace(c)) return;
                    if (c == '[')
                    {
                        _state = State.ArrayValue;
                        return;
                    }
                    _state = State.Seek;
                    continue;
                case State.ArrayValue:
                    if (IsWhitespace(c)) return;
                    if (c == '"')
                    {
                        BeginString();
                        _state = State.Element;
                    }
                    else
                    {
                        _state = State.Done;
                    }
                    return;
                case State.ArrayAfterValue:
                    if (IsWhitespace(c)) return;
                    _state = c == ',' ? State.ArrayValue : State.Done;
                    return;
                case State.Element:
                    switch (ReadStringChar(c))
                    {
                        case StringStep.Closed:
                            CompleteElement();
                            break;
                        case StringStep.Invalid:
                            _state = State.Done;
                            break;
                    }
                    return;
                default:
                    return;
            }
        }
    }

    private void CompleteElement()
    {
        var text = _value.ToString();
        if (string.IsNullOrWhiteSpace(text) || _next >= expectedCount)
        {
            _state = State.Done;
            return;
        }
        var index = _next++;
        _state = State.ArrayAfterValue;
        onTranslation(index, text);
    }

    private void BeginString()
    {
        _value.Clear();
        _escape = false;
        _unicodeDigits = -1;
    }

    private enum StringStep
    {
        Continue,
        Closed,
        Invalid,
    }

    private StringStep ReadStringChar(char c)
    {
        if (_unicodeDigits >= 0)
        {
            var digit = HexValue(c);
            if (digit < 0)
            {
                _unicodeDigits = -1;
                return StringStep.Invalid;
            }
            _unicodeValue = (_unicodeValue << 4) | digit;
            if (++_unicodeDigits == 4)
            {
                _value.Append((char)_unicodeValue);
                _unicodeDigits = -1;
            }
            return StringStep.Continue;
        }

        if (_escape)
        {
            _escape = false;
            switch (c)
            {
                case '"': _value.Append('"'); break;
                case '\\': _value.Append('\\'); break;
                case '/': _value.Append('/'); break;
                case 'b': _value.Append('\b'); break;
                case 'f': _value.Append('\f'); break;
                case 'n': _value.Append('\n'); break;
                case 'r': _value.Append('\r'); break;
                case 't': _value.Append('\t'); break;
                case 'u':
                    _unicodeDigits = 0;
                    _unicodeValue = 0;
                    break;
                default:
                    return StringStep.Invalid;
            }
            return StringStep.Continue;
        }

        switch (c)
        {
            case '\\':
                _escape = true;
                return StringStep.Continue;
            case '"':
                return StringStep.Closed;
            case < ' ':
                return StringStep.Invalid;
            default:
                _value.Append(c);
                return StringStep.Continue;
        }
    }

    private bool WatchTag(char c, string tag)
    {
        _tag.Append(char.ToLowerInvariant(c));
        if (_tag.Length > tag.Length) _tag.Remove(0, _tag.Length - tag.Length);
        if (_tag.Length != tag.Length || !_tag.Equals(tag.AsSpan())) return false;
        _tag.Clear();
        return true;
    }

    private static bool IsWhitespace(char c) => c is ' ' or '\t' or '\n' or '\r';

    private static int HexValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };
}
