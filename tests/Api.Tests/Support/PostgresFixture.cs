using Testcontainers.PostgreSql;

namespace Api.Tests.Support;

/// <summary>
/// An ephemeral PostgreSQL container for the whole test run. Nothing here reads a
/// database host from the environment: the suite must not depend on an external
/// database, which is what C45 asserts.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public string ConnectionString { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    /// <summary>
    /// A connection string for a database that does not exist yet, so a test can
    /// observe what happens on an empty schema without disturbing its neighbours.
    /// </summary>
    public string ConnectionStringForFreshDatabase(string name)
    {
        var builder = new Npgsql.NpgsqlConnectionStringBuilder(ConnectionString)
        {
            Database = name,
        };
        return builder.ConnectionString;
    }

    public async Task CreateDatabaseAsync(string name)
    {
        await using var connection = new Npgsql.NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE \"{name}\"";
        await command.ExecuteNonQueryAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
