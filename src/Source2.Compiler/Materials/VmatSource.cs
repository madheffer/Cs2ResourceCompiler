using System.Globalization;
using System.Text;

namespace Source2.Compiler.Materials;

/// <summary>
/// A <c>.vmat</c> source: KeyValues1 text, <c>Layer0 { shader "x.vfx" Key "value" ... }</c>,
/// read the way resourcecompiler reads it (GROUND_TRUTH 264).
///
/// <para>RC loads the file with tier0's <c>LoadKV3FromKV3OrKV1</c> (tier0 0923 1800cd310, KV1
/// text through 1800cccc0), which gives every KV1 value a KV3 type from its text alone, quoted
/// or not (1800cf320, see <see cref="TypeOf"/>). The material compile
/// (CResourceCompilerMaterial::CompileResourceInternal, rc 0923 18010f480) then walks the
/// root's members once: an int is an int param (18010e280), a double a float param
/// (18010e430), an empty string or one starting with <c>[</c> a vector param (18010e550).
/// Anything else is looked at only by name: the <c>attributes</c>, <c>DecalAttributes</c> and
/// <c>SystemAttributes</c> tables are material attributes (<see cref="Attributes"/>),
/// <c>ToolAttributes</c> and <c>dynamicparams</c> are read too (not ported: refused here), and
/// every other member is skipped. Texture paths are strings the param walk skips; the texture
/// inputs read them by name. RC never names <c>legacy_import</c> (the S1 material
/// source1import keeps under the shader name), so it is skipped like any other table.</para>
/// </summary>
public sealed class VmatSource
{
    /// <summary>The shader file name (<c>csgo_complex.vfx</c>).</summary>
    public required string Shader { get; init; }

    /// <summary>Every other non-table key of <c>Layer0</c>, in file order, as written.</summary>
    public required IReadOnlyList<KeyValuePair<string, string>> Values { get; init; }

    /// <summary>The material attributes from the attribute tables, in file order.</summary>
    public IReadOnlyList<VmatAttribute> Attributes { get; init; } = [];

    /// <summary>The values by name (the last one wins, as a KV1 lookup does).</summary>
    public IReadOnlyDictionary<string, string> ByName =>
        Values.GroupBy(v => v.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.Ordinal);

    /// <summary>Parse a <c>.vmat</c>.</summary>
    public static VmatSource Parse(byte[] source, string path)
    {
        var (rootName, root) = Kv1Text.Read(source, path);
        if (!string.Equals(rootName, "Layer0", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{path}: the root is '{rootName}', not Layer0; other layers are not compiled here yet.");

        string? shader = null;
        var values = new List<KeyValuePair<string, string>>();
        var attributes = new List<VmatAttribute>();
        foreach (var (key, value, table) in root)
        {
            if (table is not null)
            {
                // 18010f480: the names are compared with V_stricmp_fast.
                if (IsNamed(key, "attributes") || IsNamed(key, "DecalAttributes") || IsNamed(key, "SystemAttributes"))
                    ReadAttributes(table, IsNamed(key, "SystemAttributes"), attributes, path);
                else if (IsNamed(key, "ToolAttributes") || IsNamed(key, "dynamicparams"))
                    throw new InvalidOperationException($"{path}: '{key}' is not compiled here yet.");
                continue;
            }
            Kv1Text.RefuseEscapes(value!, path, key);
            if (string.Equals(key, "shader", StringComparison.OrdinalIgnoreCase))
                shader = value;
            else
                values.Add(new(key, value!));
        }
        return new VmatSource
        {
            Shader = shader ?? throw new InvalidOperationException($"{path} names no shader."),
            Values = values,
            Attributes = attributes,
        };
    }

    private static bool IsNamed(string key, string name) => Io.Tier0Strings.StricmpFast(key, name) == 0;

    // 18010f480's attribute loop, for each member of the table: an int is an int attribute
    // (18010e280, always tried first); in SystemAttributes that is all, elsewhere an int is a
    // float attribute as well (all four lanes the value). Otherwise a double is a float
    // attribute (18010e430), an empty or '['-string a vector attribute (18010e550), any other
    // string a string attribute; a table inside is skipped.
    private static void ReadAttributes(IReadOnlyList<Kv1Text.Member> table, bool system, List<VmatAttribute> into, string path)
    {
        foreach (var (key, text, inner) in table)
        {
            if (inner is not null)
                continue;
            Kv1Text.RefuseEscapes(text!, path, key);
            var typed = TypeOf(text!, path, key);
            switch (typed.Kind)
            {
                case Kv1Kind.Int:
                    into.Add(new(key, VmatAttributeKind.Int, text!));
                    if (!system)
                        into.Add(new(key, VmatAttributeKind.Float, text!));
                    break;
                case Kv1Kind.Double:
                    into.Add(new(key, VmatAttributeKind.Float, text!));
                    break;
                case Kv1Kind.String when text!.Length == 0 || text[0] == '[':
                    into.Add(new(key, VmatAttributeKind.Vector, text));
                    break;
                case Kv1Kind.String:
                    into.Add(new(key, VmatAttributeKind.String, text));
                    break;
            }
        }
    }

    /// <summary>The KV3 type tier0 gives a KV1 value (1800cf320).</summary>
    public enum Kv1Kind
    {
        /// <summary>A string (the text as it is).</summary>
        String,
        /// <summary>An int64 that fits int32.</summary>
        Int,
        /// <summary>A double, stored as <c>(double)(float)strtod</c>.</summary>
        Double,
    }

    /// <summary>A typed KV1 value.</summary>
    public readonly record struct Kv1Value(Kv1Kind Kind, long Int, double Double);

    /// <summary>
    /// tier0 1800cf320: <c>strtoll(s, &amp;ie, 10)</c>, then (errno cleared) <c>strtod(s, &amp;fe)</c>.
    /// An empty text is a string; 18 characters starting <c>0x</c> a uint64 (refused here: no
    /// material param reads one); a double when <c>ie &lt; fe</c>, <c>fe</c> is the end and
    /// strtod set no ERANGE, as <c>(double)(float)</c> the value; an int when <c>ie</c> is the end
    /// and the value fits int32; a string otherwise. Texts whose strtod reading is not plain
    /// decimal (hex, inf, nan) or that over- or underflow are refused rather than guessed.
    /// </summary>
    public static Kv1Value TypeOf(string s, string path, string key)
    {
        if (s.Length == 0)
            return new(Kv1Kind.String, 0, 0);
        if (s.Length == 18 && s[0] == '0' && s[1] == 'x')
            throw new InvalidOperationException($"{path}: '{key}' is \"{s}\", a uint64 to tier0; not compiled here.");
        int intEnd = StrtollEnd(s, out var l);
        int dblEnd = StrtodEnd(s, out var d, out var special, path, key);
        // Only a strtod reading that ends at the end can make a double, so a special form
        // (hex, inf, nan) that cannot be the whole text types the value as a string either way.
        if (special && SpecialCouldBeWhole(s))
            throw new InvalidOperationException($"{path}: '{key}' is \"{s}\", which strtod may read whole as hex, inf or nan; not compiled here.");
        if (special)
            return new(Kv1Kind.String, 0, 0);
        if (intEnd < dblEnd && dblEnd == s.Length)
            return new(Kv1Kind.Double, 0, (float)d);
        if (intEnd == s.Length && l >= int.MinValue && l <= int.MaxValue)
            return new(Kv1Kind.Int, l, 0);
        return new(Kv1Kind.String, 0, 0);
    }

    // C isspace in the "C" locale.
    private static bool IsSpace(char c) => c is ' ' or '\t' or '\n' or '\v' or '\f' or '\r';

    // strtoll base 10 (ucrt, 64-bit: tier0 1802c29a4 -> 1802c1f50): where it stops (0 when nothing
    // converts) and the value, clamped as strtoll clamps.
    private static int StrtollEnd(string s, out long value)
    {
        value = 0;
        int i = 0;
        while (i < s.Length && IsSpace(s[i])) i++;
        bool negative = false;
        if (i < s.Length && (s[i] == '+' || s[i] == '-')) negative = s[i++] == '-';
        int digits = i;
        decimal magnitude = 0;
        while (i < s.Length && s[i] is >= '0' and <= '9')
        {
            if (magnitude <= (decimal)ulong.MaxValue)
                magnitude = magnitude * 10 + (s[i] - '0');
            i++;
        }
        if (i == digits)
            return 0;
        var signed = negative ? -magnitude : magnitude;
        value = signed > long.MaxValue ? long.MaxValue : signed < long.MinValue ? long.MinValue : (long)signed;
        return i;
    }

    // strtod (ucrt common_strtod_l, tier0 1802c1470): where it stops (0 when nothing converts).
    // special: the text after white space and sign starts inf, nan or 0x, forms strtod reads
    // that are not ported (nothing is read then).
    private static int StrtodEnd(string s, out double value, out bool special, string path, string key, bool wholeMatters = false)
    {
        value = 0;
        int i = 0;
        while (i < s.Length && IsSpace(s[i])) i++;
        int start = i;
        if (i < s.Length && (s[i] == '+' || s[i] == '-')) i++;
        int body = i;
        special = StartsWithIgnoreCase(s, body, "inf") || StartsWithIgnoreCase(s, body, "nan")
            || (body + 1 < s.Length && s[body] == '0' && (s[body + 1] == 'x' || s[body + 1] == 'X'));
        if (special)
            return 0;
        int mantissaDigits = 0;
        bool nonZero = false;
        while (i < s.Length && s[i] is >= '0' and <= '9') { nonZero |= s[i] != '0'; i++; mantissaDigits++; }
        if (i < s.Length && s[i] == '.')
        {
            i++;
            while (i < s.Length && s[i] is >= '0' and <= '9') { nonZero |= s[i] != '0'; i++; mantissaDigits++; }
        }
        if (mantissaDigits == 0)
            return 0;
        if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
        {
            int e = i + 1;
            if (e < s.Length && (s[e] == '+' || s[e] == '-')) e++;
            if (e < s.Length && s[e] is >= '0' and <= '9')
            {
                while (e < s.Length && s[e] is >= '0' and <= '9') e++;
                i = e;
            }
        }
        value = double.Parse(s.AsSpan(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
        // Where strtod sets ERANGE (overflow certainly; which underflows is not read) decides the
        // type only for a reading that is the whole text.
        if ((double.IsInfinity(value) || (nonZero && (value == 0 || double.IsSubnormal(value)))) && (i == s.Length || wholeMatters))
            throw new InvalidOperationException($"{path}: '{key}' is \"{s}\", out of a double's normal range; how strtod's ERANGE types it is not ported.");
        return i;
    }

    // A superset of the texts ucrt's strtod reads whole as hex, inf or nan: white space, a
    // sign, then inf / infinity / nan / nan(chars), or 0x and hex-float characters only.
    private static bool SpecialCouldBeWhole(string s)
    {
        var t = s.TrimStart(' ', '\t', '\n', '\v', '\f', '\r');
        if (t.Length > 0 && (t[0] == '+' || t[0] == '-'))
            t = t[1..];
        if (t.Equals("inf", StringComparison.OrdinalIgnoreCase) || t.Equals("infinity", StringComparison.OrdinalIgnoreCase)
            || t.Equals("nan", StringComparison.OrdinalIgnoreCase))
            return true;
        if (StartsWithIgnoreCase(t, 0, "nan(") && t.EndsWith(')'))
            return t[4..^1].All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
        if (t.Length >= 2 && t[0] == '0' && (t[1] == 'x' || t[1] == 'X'))
            return t[2..].All(c => char.IsAsciiHexDigit(c) || c is '.' or 'p' or 'P' or '+' or '-');
        return false;
    }

    private static bool StartsWithIgnoreCase(string s, int at, string what) =>
        at + what.Length <= s.Length && string.Compare(s, at, what, 0, what.Length, StringComparison.OrdinalIgnoreCase) == 0;

    /// <summary>
    /// A vector value: rc 18010e550 reads a string with <c>V_sscanf(s, "[%f %f %f %f]")</c> into
    /// four zeros, so the lanes stop at the first one that does not scan (an empty string is all
    /// zeros).
    /// </summary>
    public static float[] ParseVector(string s, string path, string key)
    {
        var v = new float[4];
        if (s.Length == 0)
            return v;
        if (s[0] != '[')
            throw new InvalidOperationException($"{path}: '{key}' is not a vector: {s}");
        int i = 1;
        for (int lane = 0; lane < 4; lane++)
        {
            // " " in the format: any run of white space; %f skips more before its number.
            while (i < s.Length && IsSpace(s[i])) i++;
            int end = StrtodEnd(s[i..], out _, out var special, path, key, wholeMatters: true);
            if (special)
                throw new InvalidOperationException($"{path}: '{key}' has a hex, inf or nan lane ({s}); how %f reads it is not ported.");
            if (end == 0)
                break;
            // The lane as float.Parse rounds the text (once, to float). Whether ucrt's %f rounds
            // once or through a double is not read; the two differ only on double-rounding ties.
            v[lane] = float.Parse(s.AsSpan(i, end).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);
            i += end;
        }
        return v;
    }
}

/// <summary>What kind of material attribute an attribute-table member is.</summary>
public enum VmatAttributeKind
{
    /// <summary>m_intAttributes.</summary>
    Int,
    /// <summary>m_floatAttributes.</summary>
    Float,
    /// <summary>m_vectorAttributes.</summary>
    Vector,
    /// <summary>m_stringAttributes.</summary>
    String,
}

/// <summary>A member of a material attribute table, with the kind RC files it under.</summary>
public sealed record VmatAttribute(string Name, VmatAttributeKind Kind, string Text);

/// <summary>
/// KeyValues1 text with every value kept as its text, for <see cref="VmatSource"/>: tier0 types
/// the text itself (<see cref="VmatSource.TypeOf"/>), so a reader that types numbers on its own
/// (ValveKeyValue 0.70 gives <c>"1.000"</c> back as <c>1</c>) cannot feed it.
///
/// <para>tier0 reads KV1 with its shared text tokenizer (1800cccc0 -> 1800cdce0 -> 1800cf9c0 /
/// 1800ce210), which is not ported. This reads the part of the syntax KV1 readers agree on and
/// refuses the rest: quoted strings (no <c>\"</c>, no line break inside), unquoted tokens of
/// letters, digits and <c>_ . / - +</c>, braces, <c>//</c> comments to the end of the line, a
/// UTF-8 BOM (1800cccc0 skips it). <c>#</c> directives, <c>=</c> (tier0 reads
/// <c>key = value</c>), <c>/*</c>, conditionals, UTF-16, text after the root and a key twice in
/// one table (how the KV3 table keeps it is not read) are refused.</para>
/// </summary>
internal static class Kv1Text
{
    /// <summary>A table member: a value (<c>Table</c> null) or a table (<c>Value</c> null).</summary>
    public readonly record struct Member(string Key, string? Value, IReadOnlyList<Member>? Table);

    /// <summary>The root key and its table.</summary>
    public static (string Name, IReadOnlyList<Member> Table) Read(byte[] source, string path)
    {
        if (source.Length >= 2 && source[0] == 0xFF && source[1] == 0xFE)
            throw new InvalidOperationException($"{path} is UTF-16; tier0's conversion of it is not ported.");
        int skip = source.Length >= 3 && source[0] == 0xEF && source[1] == 0xBB && source[2] == 0xBF ? 3 : 0;
        var text = Encoding.UTF8.GetString(source, skip, source.Length - skip);
        var reader = new Reader(text, path);
        var name = reader.Next() ?? throw new InvalidOperationException($"{path} is empty.");
        if (name.Kind != TokenKind.Text)
            throw reader.Error($"expected the root key, found '{name.Text}'");
        if (reader.Next() is not { Kind: TokenKind.Open })
            throw reader.Error("expected { after the root key");
        var table = reader.Table();
        if (reader.Next() is { } extra)
            throw reader.Error($"'{extra.Text}' after the root table is not read here");
        return (name.Text, table);
    }

    /// <summary>Refuse a backslash in a value the compile reads: whether tier0 reads it as an
    /// escape (LoadKV3FromKV3OrKV1 passes KV1TextEscapeBehavior 0) is not read.</summary>
    public static void RefuseEscapes(string value, string path, string key)
    {
        if (value.Contains('\\'))
            throw new InvalidOperationException($"{path}: '{key}' has a backslash; tier0's escape handling is not ported.");
    }

    private enum TokenKind { Text, Open, Close }

    private sealed record Token(TokenKind Kind, string Text);

    private sealed class Reader(string text, string path)
    {
        private int _at;
        private int _line = 1;

        public InvalidOperationException Error(string what) => new($"{path}({_line}): {what}.");

        public List<Member> Table()
        {
            var members = new List<Member>();
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (true)
            {
                var key = Next() ?? throw Error("the file ends inside a table");
                if (key.Kind == TokenKind.Close)
                    return members;
                if (key.Kind != TokenKind.Text)
                    throw Error($"Bad keyname: '{key.Text}'");
                if (key.Text.Contains('\\'))
                    throw Error($"the key '{key.Text}' has a backslash; tier0's key escapes are not ported");
                if (!keys.Add(key.Text))
                    throw Error($"'{key.Text}' is in the table twice; how tier0's KV3 table keeps it is not read");
                var value = Next() ?? throw Error($"'{key.Text}' has no value");
                if (value.Kind == TokenKind.Open)
                    members.Add(new(key.Text, null, Table()));
                else if (value.Kind == TokenKind.Text)
                    members.Add(new(key.Text, value.Text, null));
                else
                    throw Error($"'{key.Text}' has no value");
            }
        }

        public Token? Next()
        {
            while (_at < text.Length)
            {
                char c = text[_at];
                if (c is ' ' or '\t' or '\r')
                    _at++;
                else if (c == '\n')
                {
                    _at++;
                    _line++;
                }
                else if (c == '/' && _at + 1 < text.Length && text[_at + 1] == '/')
                {
                    while (_at < text.Length && text[_at] != '\n')
                        _at++;
                }
                else
                    break;
            }
            if (_at >= text.Length)
                return null;
            char first = text[_at];
            if (first == '{') { _at++; return new(TokenKind.Open, "{"); }
            if (first == '}') { _at++; return new(TokenKind.Close, "}"); }
            if (first == '"')
            {
                int end = _at + 1;
                while (end < text.Length && text[end] != '"')
                {
                    if (text[end] == '\n')
                        throw Error("a quoted string runs past the end of its line");
                    end++;
                }
                if (end >= text.Length)
                    throw Error("a quoted string has no end");
                if (text[end - 1] == '\\')
                    throw Error("a quoted string has \\\"; tier0's escape handling is not ported");
                var quoted = text[(_at + 1)..end];
                _at = end + 1;
                return new(TokenKind.Text, quoted);
            }
            int start = _at;
            while (_at < text.Length && (char.IsAsciiLetterOrDigit(text[_at]) || text[_at] is '_' or '.' or '/' or '-' or '+'))
            {
                if (text[_at] == '/' && _at + 1 < text.Length && text[_at + 1] == '/')
                    throw Error("'//' inside an unquoted token");
                _at++;
            }
            if (_at == start || (_at < text.Length && !(text[_at] is ' ' or '\t' or '\r' or '\n' or '{' or '}' or '"')))
                throw Error($"'{text[_at]}' is outside the KV1 syntax read here");
            return new(TokenKind.Text, text[start.._at]);
        }
    }
}
