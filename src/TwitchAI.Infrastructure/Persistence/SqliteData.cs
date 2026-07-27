using System.Globalization;
using Microsoft.Data.Sqlite;

namespace TwitchAI.Infrastructure.Persistence;

internal static class SqliteData
{
    public static DateTimeOffset GetDateTimeOffset(SqliteDataReader reader, int ordinal)
    {
        return ParseDateTimeOffset(reader.GetString(ordinal));
    }

    public static DateTimeOffset ParseDateTimeOffset(string value)
    {
        return DateTimeOffset.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
    }

    public static DateTimeOffset? GetNullableDateTimeOffset(SqliteDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? null : GetDateTimeOffset(reader, ordinal);
    }

    public static string ToDatabaseTimestamp(DateTimeOffset value)
    {
        return value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    }

    public static object DbValue(string? value)
    {
        return value is null ? DBNull.Value : value;
    }

    public static object DbValue(int? value)
    {
        return value.HasValue ? value.Value : DBNull.Value;
    }

    public static object DbValue(long? value)
    {
        return value.HasValue ? value.Value : DBNull.Value;
    }

    public static object DbValue(double? value)
    {
        return value.HasValue ? value.Value : DBNull.Value;
    }
}
