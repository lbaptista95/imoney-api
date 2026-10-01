using System.Net;
using Api.Infrastructure.Persistence;
using Api.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Api.Tests;

/// <summary>S1: the skeleton comes up locally with synthetic data (C2-C6, C53).</summary>
[Collection(PostgresCollection.Name)]
public sealed class LocalEnvironmentTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// A port nothing listens on, so "the database is unreachable" is produced rather
    /// than simulated. Port 1 is privileged and unused.
    /// </summary>
    private const string UnreachableDatabase =
        "Host=127.0.0.1;Port=1;Database=imoney;Username=imoney;Password=irrelevant;Timeout=2;Command Timeout=2";

    [Fact]
    public async Task DevelopmentAppliesPendingMigrationsBeforeServing()
    {
        var database = $"migrate_{Guid.NewGuid():N}";
        await postgres.CreateDatabaseAsync(database);
        var connectionString = postgres.ConnectionStringForFreshDatabase(database);

        await using var factory = new ApiFactory(connectionString);
        var client = factory.CreateClient();

        var health = await client.GetAsync("/health", Ct);

        // The order is the claim: by the time the first request is answered, the
        // schema is already there. Asserting the table afterwards would pass even if
        // the migration ran lazily on first query.
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.True(await TableExistsAsync(connectionString, "accounts"));
        Assert.True(await TableExistsAsync(connectionString, "transactions"));
    }

    [Fact]
    public async Task ProductionDoesNotApplyMigrations()
    {
        var database = $"prod_{Guid.NewGuid():N}";
        await postgres.CreateDatabaseAsync(database);
        var connectionString = postgres.ConnectionStringForFreshDatabase(database);

        await using var factory = new ApiFactory(connectionString, environment: "Production");
        var client = factory.CreateClient();

        var health = await client.GetAsync("/health", Ct);

        // The application served a request and still did not migrate. Without this
        // first assertion the test would also pass if it had never started, which
        // would make "no migration ran" true for the wrong reason. The database is
        // reachable here, just empty, so the health check itself is fine.
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.False(await TableExistsAsync(connectionString, "accounts"));
    }

    [Fact]
    public async Task HealthReturns200WhenDatabaseReachable()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        var client = factory.CreateClient();

        var response = await client.GetAsync("/health", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task HealthReturns503WhenDatabaseUnreachable()
    {
        await using var factory = new ApiFactory(UnreachableDatabase, environment: "Production");
        var client = factory.CreateClient();

        var response = await client.GetAsync("/health", Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task ApiSurvivesDatabaseOutage()
    {
        await using var factory = new ApiFactory(UnreachableDatabase, environment: "Production");
        var client = factory.CreateClient();

        var first = await client.GetAsync("/health", Ct);
        var second = await client.GetAsync("/health", Ct);

        // The second call is the claim: the process answered again instead of having
        // died with the first failure.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, first.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, second.StatusCode);
    }

    [Fact]
    public async Task CategoriesTableExistsWithOptionalForeignKey()
    {
        var database = $"categories_{Guid.NewGuid():N}";
        await postgres.CreateDatabaseAsync(database);
        var connectionString = postgres.ConnectionStringForFreshDatabase(database);

        await using var factory = new ApiFactory(connectionString);
        factory.CreateClient();

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Assert.True(await TableExistsAsync(connectionString, "categories"));

        var account = new Features.Accounts.Account
        {
            Id = Guid.NewGuid(),
            Provider = "test",
            ProviderAccountId = "acc-1",
            Kind = Features.Accounts.AccountKind.CHECKING,
            Name = "Conta",
            Balance = 10m,
        };
        db.Accounts.Add(account);
        await db.SaveChangesAsync(Ct);

        // Null is accepted: a transaction with no category is valid.
        db.Transactions.Add(NewTransaction(account.Id, "txn-null", categoryId: null));
        await db.SaveChangesAsync(Ct);

        // A category that does not exist is rejected by the foreign key.
        db.Transactions.Add(NewTransaction(account.Id, "txn-ghost", categoryId: Guid.NewGuid()));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
        db.ChangeTracker.Clear();

        // An existing category is accepted.
        var category = new Features.Categories.Category { Id = Guid.NewGuid(), Name = "Mercado" };
        db.Categories.Add(category);
        await db.SaveChangesAsync(Ct);
        db.Transactions.Add(NewTransaction(account.Id, "txn-real", categoryId: category.Id));
        await db.SaveChangesAsync(Ct);

        Assert.Equal(2, await db.Transactions.CountAsync(Ct));
    }

    private static Features.Transactions.Transaction NewTransaction(
        Guid accountId,
        string providerTransactionId,
        Guid? categoryId) => new()
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Provider = "test",
            ProviderTransactionId = providerTransactionId,
            Type = Features.Transactions.TransactionType.DEBIT,
            Amount = 12.34m,
            OccurredAt = DateTimeOffset.UtcNow,
            Status = Features.Transactions.TransactionStatus.POSTED,
            CategoryId = categoryId,
        };

    private static async Task<bool> TableExistsAsync(string connectionString, string table)
    {
        await using var connection = new Npgsql.NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT to_regclass(@name) IS NOT NULL";
        command.Parameters.AddWithValue("name", $"public.{table}");
        return (bool)(await command.ExecuteScalarAsync(Ct))!;
    }
}
