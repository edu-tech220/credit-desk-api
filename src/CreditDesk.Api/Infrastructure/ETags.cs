namespace CreditDesk.Api.Infrastructure;

public static class ETags
{
    public static string Format(Guid version) => $"\"{version:N}\"";

    /// <summary>Extrae la versión de un If-Match (<c>"abc"</c> o <c>W/"abc"</c>). Vacío o <c>*</c> = sin precondición.</summary>
    public static string? Parse(string? ifMatch)
    {
        if (string.IsNullOrWhiteSpace(ifMatch) || ifMatch.Trim() == "*")
            return null;

        var value = ifMatch.Trim();
        if (value.StartsWith("W/", StringComparison.Ordinal))
            value = value[2..];
        return value.Trim('"');
    }
}

public static class RateLimitPolicies
{
    public const string Auth = "auth";
}
