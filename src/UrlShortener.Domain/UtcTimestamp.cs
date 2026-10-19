namespace UrlShortener.Domain;

internal static class UtcTimestamp
{
    public static void RequireUtc(DateTime value, string parameterName)
    {
        if (value.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Timestamp must have DateTimeKind.Utc.", parameterName);
        }
    }
}
