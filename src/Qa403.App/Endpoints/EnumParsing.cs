namespace Qa403.App.Endpoints;

/// <summary>
/// Shared parsing for the string-valued enums the REST API accepts. Requests carry enum names
/// (JsonStringEnumConverter, see Program.cs), and a bad name must produce a 400 that says what the
/// valid names are rather than silently falling back to a default.
/// </summary>
internal static class EnumParsing
{
    /// <summary>
    /// Parses <paramref name="raw"/> case-insensitively. A <c>null</c> value is not an error — it
    /// means "leave this alone" and yields <paramref name="fallback"/>, which is what makes the
    /// patch-style config requests work.
    /// </summary>
    public static bool TryParse<TEnum>(string? raw, TEnum fallback, out TEnum value, out string error)
        where TEnum : struct, Enum
    {
        error = "";
        if (raw is null)
        {
            value = fallback;
            return true;
        }

        if (Enum.TryParse(raw, ignoreCase: true, out value))
            return true;

        error = $"Unknown {typeof(TEnum).Name} '{raw}'. Valid values: {string.Join(", ", Enum.GetNames<TEnum>())}.";
        return false;
    }
}
