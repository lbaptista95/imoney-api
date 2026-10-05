namespace Api.Infrastructure.Persistence;

/// <summary>Where the database connection string comes from, in one place.</summary>
public static class DatabaseConfiguration
{
    /// <summary>
    /// Used when nothing is configured. Not fail-fast on purpose: the OpenAPI
    /// generator builds this application at build time with no database, and an
    /// unconfigured database is meant to surface as an unhealthy /health and a 500
    /// from a data route, not as a build that cannot run.
    /// </summary>
    private const string Unconfigured = "Host=localhost;Database=imoney;Username=imoney";

    public static string ConnectionStringFrom(IConfiguration configuration) =>
        configuration.GetConnectionString("Default") is { Length: > 0 } configured
            ? configured
            : Unconfigured;
}
