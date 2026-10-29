using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Resourcefull;

/// <summary>
/// An amount of stored content, such as a <see cref="ResourceContainerPolicy"/> limit. Units are 1,024-based, as
/// Windows shows file sizes: <c>ResourceSize.Megabytes(1)</c> is 1,048,576 bytes. A number of bytes converts to it,
/// and configuration binds it from text such as <c>"5 GB"</c>, <c>"100MB"</c> or <c>"1.5 GB"</c>.
/// </summary>
[TypeConverter(typeof(ResourceSizeConverter))]
public readonly partial record struct ResourceSize : IParsable<ResourceSize>
{
    private static readonly (string Unit, long Bytes)[] Units =
        [("TB", 1L << 40), ("GB", 1L << 30), ("MB", 1L << 20), ("KB", 1L << 10)];

    /// <summary>A size of <paramref name="bytes"/> bytes.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bytes"/> is negative.</exception>
    public ResourceSize(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        Bytes = bytes;
    }

    /// <summary>The size in bytes.</summary>
    public long Bytes { get; }

    /// <summary>A size of <paramref name="kilobytes"/> × 1,024 bytes.</summary>
    public static ResourceSize Kilobytes(long kilobytes) => new(checked(kilobytes * (1L << 10)));

    /// <summary>A size of <paramref name="megabytes"/> × 1,024² bytes.</summary>
    public static ResourceSize Megabytes(long megabytes) => new(checked(megabytes * (1L << 20)));

    /// <summary>A size of <paramref name="gigabytes"/> × 1,024³ bytes.</summary>
    public static ResourceSize Gigabytes(long gigabytes) => new(checked(gigabytes * (1L << 30)));

    /// <summary>A size of <paramref name="terabytes"/> × 1,024⁴ bytes.</summary>
    public static ResourceSize Terabytes(long terabytes) => new(checked(terabytes * (1L << 40)));

    /// <summary>A size of <paramref name="bytes"/> bytes.</summary>
    public static implicit operator ResourceSize(long bytes) => new(bytes);

    /// <summary>
    /// Reads a size such as <c>"5 GB"</c>, <c>"100MB"</c>, <c>"1.5 GB"</c> or <c>"2048"</c> (bytes): a number, with or
    /// without a decimal point, then optionally <c>B</c>, <c>KB</c>, <c>MB</c>, <c>GB</c> or <c>TB</c> (or <c>KiB</c>
    /// and so on, which mean the same), in any case. A fraction of a byte is rounded to the nearest.
    /// </summary>
    /// <param name="s">The text to read.</param>
    /// <param name="provider">Ignored: the decimal point is always <c>.</c>, so configuration reads the same everywhere.</param>
    /// <exception cref="FormatException"><paramref name="s"/> isn't a size.</exception>
    public static ResourceSize Parse(string s, IFormatProvider? provider = null) =>
        TryParse(s, provider, out ResourceSize size)
            ? size
            : throw new FormatException($"\"{s}\" isn't a size; write it like \"5 GB\", \"100MB\" or \"2048\" (bytes).");

    /// <inheritdoc cref="Parse(string, IFormatProvider?)"/>
    /// <returns>Whether <paramref name="s"/> is a size.</returns>
    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, out ResourceSize result)
    {
        result = default;
        Match match = s is null ? Match.Empty : SizeText().Match(s);
        if (!match.Success || !decimal.TryParse(match.Groups["number"].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal number))
            return false;

        string unit = match.Groups["unit"].Value.ToUpperInvariant().Replace("I", "", StringComparison.Ordinal);
        long multiplier = unit is "" or "B" ? 1 : Units.Single(u => u.Unit == unit).Bytes;
        try
        {
            result = new ResourceSize((long)Math.Round(number * multiplier, MidpointRounding.AwayFromZero));
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    /// <summary>The size in the largest unit it's a whole number of, such as <c>5 GB</c>, or in bytes, such as <c>1500 B</c>.</summary>
    public override string ToString()
    {
        long bytes = Bytes;
        foreach ((string unit, long size) in Units)
        {
            if (bytes >= size && bytes % size == 0) return $"{bytes / size} {unit}";
        }

        return $"{bytes} B";
    }

    [GeneratedRegex(@"^\s*(?<number>\d+(\.\d+)?)\s*(?<unit>[KMGT]i?B|B)?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SizeText();
}

/// <summary>Converts text such as <c>"5 GB"</c> to a <see cref="ResourceSize"/>, so configuration can bind one.</summary>
internal sealed class ResourceSizeConverter : TypeConverter
{
    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType) =>
        sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);

    public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value) =>
        value is string text ? ResourceSize.Parse(text, culture) : base.ConvertFrom(context, culture, value);
}
