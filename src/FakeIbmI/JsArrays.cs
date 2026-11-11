// Typed-array operations with the clamping rules of the JavaScript engine this was ported
// from (fill, copyWithin, subarray), so edge cases behave byte for byte the same.

namespace FakeIbmI;

internal static class JsArrays
{
    private static int Clamp(int index, int length) =>
        index < 0 ? Math.Max(length + index, 0) : Math.Min(index, length);

    /// <summary>Uint8Array.prototype.fill(value, start, end).</summary>
    public static void Fill(Span<byte> span, byte value, int start, int end)
    {
        var from = Clamp(start, span.Length);
        var to = Clamp(end, span.Length);
        if (to > from) span[from..to].Fill(value);
    }

    /// <summary>Uint8Array.prototype.copyWithin(target, start, end).</summary>
    public static void CopyWithin(Span<byte> span, int target, int start, int end)
    {
        var to = Clamp(target, span.Length);
        var from = Clamp(start, span.Length);
        var final = Clamp(end, span.Length);
        var count = Math.Min(final - from, span.Length - to);
        if (count > 0) span.Slice(from, count).CopyTo(span.Slice(to, count));
    }

    /// <summary>Uint8Array.prototype.subarray(begin, end), as a span.</summary>
    public static ReadOnlySpan<byte> Sub(ReadOnlySpan<byte> span, int begin, int end)
    {
        var from = Clamp(begin, span.Length);
        var to = Clamp(end, span.Length);
        return to > from ? span[from..to] : [];
    }

    /// <summary>Uint8Array.prototype.subarray(begin, end), as a writable span.</summary>
    public static Span<byte> Sub(Span<byte> span, int begin, int end)
    {
        var from = Clamp(begin, span.Length);
        var to = Clamp(end, span.Length);
        return to > from ? span[from..to] : [];
    }

    /// <summary>An element, or undefined (null) past the end.</summary>
    public static int? At(ReadOnlySpan<byte> span, int index) =>
        index >= 0 && index < span.Length ? span[index] : null;

    /// <summary>String.prototype.trimEnd: JavaScript's white space, which differs from .NET's (U+0085, U+FEFF).</summary>
    public static string TrimEnd(string text)
    {
        static bool IsJsSpace(char c) =>
            c is '\t' or '\n' or '\v' or '\f' or '\r' or ' ' or '\u00a0' or '\u1680' or (>= '\u2000' and <= '\u200a')
                or '\u2028' or '\u2029' or '\u202f' or '\u205f' or '\u3000' or '\ufeff';
        var end = text.Length;
        while (end > 0 && IsJsSpace(text[end - 1])) end--;
        return text[..end];
    }

    public static byte[] Concat(IEnumerable<byte[]> chunks)
    {
        var list = chunks.ToList();
        var output = new byte[list.Sum(c => c.Length)];
        var offset = 0;
        foreach (var chunk in list)
        {
            chunk.CopyTo(output, offset);
            offset += chunk.Length;
        }
        return output;
    }
}
