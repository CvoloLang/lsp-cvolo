using System.Globalization;
using Cvolo.LanguageServer.Core.Backend;

namespace Cvolo.LanguageServer.Protocol;

/// <summary>
/// Renders the compiler's field storage facts as annotation text. The numbers always come from the
/// compiler; only the notation is chosen here, so the same value reads as <c>24</c>, <c>0x18</c> or
/// <c>24 (0x18)</c> without ever being rounded, re-derived or re-measured. The detailed layout view
/// formats its rows the same way, so one offset format setting reads consistently across the viewer,
/// the field CodeLens and the field inlay hint.
/// </summary>
internal static class LayoutTextFormatter
{
    /// <summary>
    /// The separator between the facts of one annotation, matching the compiler's own text.
    /// <para>
    /// A comma, never <c>|</c>: an editor joins the several CodeLenses of one line with its own
    /// <c>|</c>, so a pipe inside a lens title makes one lens read as two and hides where one ends.
    /// </para>
    /// </summary>
    private const char Separator = ',';

    /// <summary>
    /// Writes one byte count. Decimal keeps the byte unit, as the compact annotations always have;
    /// hexadecimal drops it, because <c>0x08</c> is a byte count already and <c>0x08B</c> would read
    /// as one longer hex number. The combined form keeps the unit with the decimal value and puts the
    /// hexadecimal value after it, so neither notation can be mistaken for part of the other.
    /// Hexadecimal is zero padded to at least two digits, so a small layout keeps a stable width and
    /// a larger one simply grows.
    /// </summary>
    public static string Value(long value, BackendOffsetFormat format, bool withUnit = false) => format switch
    {
        BackendOffsetFormat.Hex => Hex(value),
        BackendOffsetFormat.DecimalAndHex => (withUnit ? value.ToString(CultureInfo.InvariantCulture) + "B " : value.ToString(CultureInfo.InvariantCulture) + " ") + "(" + Hex(value) + ")",
        _ => withUnit ? value.ToString(CultureInfo.InvariantCulture) + "B" : value.ToString(CultureInfo.InvariantCulture),
    };

    private static string Hex(long value) => "0x" + value.ToString("x2", CultureInfo.InvariantCulture);

    /// <summary>
    /// The field layout facts beside a declaration, where every value is a byte count:
    /// <c>offset 4B, size 4B, align 4B, pad 3B before</c>.
    /// </summary>
    public static string FieldLayout(BackendFieldLayoutInfo field, BackendOffsetFormat format) =>
        Facts(field, format, withUnit: true);

    /// <summary>
    /// The same facts as an inlay hint, where the values are bare byte counts because the hint already
    /// sits on a field declaration: <c>offset 4, size 4, align 4, pad 3 before</c>.
    /// </summary>
    public static string FieldLayoutHint(BackendFieldLayoutInfo field, BackendOffsetFormat format) =>
        Facts(field, format, withUnit: false);

    /// <summary>
    /// The padding before the field is mentioned only when the compiler inserted one, because a field
    /// that needs no padding has nothing to say about it. Tail padding is left to the type summary and
    /// the detailed view, so the gap after the last field is never reported twice.
    /// </summary>
    private static string Facts(BackendFieldLayoutInfo field, BackendOffsetFormat format, bool withUnit)
    {
        var text = string.Create(
            CultureInfo.InvariantCulture,
            $"offset {Value(field.Offset, format, withUnit)}{Separator} size {Value(field.Size, format, withUnit)}{Separator} align {Value(field.Alignment, format, withUnit)}");

        return field.PaddingBefore > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{text}{Separator} pad {Value(field.PaddingBefore, format, withUnit)} before")
            : text;
    }
}
