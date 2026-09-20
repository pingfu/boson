using System.Globalization;

namespace Boson.Util;

/// <summary>
/// SQLite writes `datetime('now')`, which is UTC with no marker on it, so
/// every reader has to supply the timezone the column does not carry.
/// </summary>
public static class DbTime
{
    public static DateTimeOffset? Parse(string? dbTime) =>
        DateTime.TryParse(dbTime, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? new DateTimeOffset(DateTime.SpecifyKind(parsed, DateTimeKind.Utc))
            : null;
}
