using Npgsql;

namespace Api.Infrastructure.Persistence;

/// <summary>
/// Strips credentials out of text that is about to be printed or logged. This exists
/// because the thing most likely to quote a connection string is the exception that
/// says it could not connect, which is exactly the text AC 8 asks us to print.
/// </summary>
public static class ConnectionSecretRedactor
{
    private const string Mask = "***";

    public static string Redact(string text, string? connectionString)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var redacted = text;

        foreach (var secret in SecretsOf(connectionString))
        {
            redacted = redacted.Replace(secret, Mask, StringComparison.Ordinal);
        }

        return redacted;
    }

    /// <summary>
    /// Every value worth masking: the password, and the whole connection string, since
    /// the string itself carries the password inside it.
    /// </summary>
    private static IEnumerable<string> SecretsOf(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            yield break;
        }

        yield return connectionString;

        string? password = null;
        try
        {
            password = new NpgsqlConnectionStringBuilder(connectionString).Password;
        }
        catch (ArgumentException)
        {
            // An unparseable connection string still gets masked as a whole, above.
        }

        if (!string.IsNullOrEmpty(password))
        {
            yield return password;
        }
    }
}
