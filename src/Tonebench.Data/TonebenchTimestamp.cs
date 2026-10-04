using System.Globalization;

namespace Tonebench.Data;

/// <summary>
/// The single ISO-8601 UTC timestamp format used throughout the schema — fixed and lexically
/// sortable, so a date-range filter is a plain indexed string comparison
/// (<c>ts &gt;= @from AND ts &lt; @to</c>) rather than a scan through <c>date()</c> conversions.
/// A stray local-time or default <c>ToString()</c> write would silently break every comparison
/// after it, which is why every write path in this project goes through here.
/// </summary>
public static class TonebenchTimestamp
{
    public const string Format = "yyyy-MM-ddTHH:mm:ss.fff'Z'";

    public static string Now() => FormatUtc(DateTimeOffset.UtcNow);

    public static string FormatUtc(DateTimeOffset value) =>
        value.UtcDateTime.ToString(Format, CultureInfo.InvariantCulture);

    public static DateTimeOffset Parse(string value) =>
        DateTimeOffset.ParseExact(value, Format, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
}
