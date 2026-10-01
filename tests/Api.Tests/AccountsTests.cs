using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Api.Features.Accounts;
using Api.Infrastructure.Persistence;
using Api.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Api.Tests;

/// <summary>S3: GET /v1/accounts lists the synthetic accounts (C22-C25).</summary>
[Collection(PostgresCollection.Name)]
public sealed class AccountsTests(PostgresFixture postgres)
{
    private const string Token = "accounts-test-token";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AccountsReturnsEverySeededAccountWithAllFields()
    {
        var connectionString = await SeededDatabaseAsync();
        await using var factory = new ApiFactory(connectionString, sharedToken: Token);
        var client = Authorized(factory);

        var response = await client.GetAsync("/v1/accounts", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(JsonValueKind.Array, body.RootElement.ValueKind);

        var expected = await SeededAccountCountAsync(connectionString);
        Assert.Equal(expected, body.RootElement.GetArrayLength());

        // Every one of the five fields, on every element: a field missing from one
        // row is still a broken contract for the client that reads that row.
        string[] fields = ["id", "provider", "kind", "name", "balance"];
        foreach (var account in body.RootElement.EnumerateArray())
        {
            foreach (var field in fields)
            {
                Assert.True(account.TryGetProperty(field, out _), $"account is missing '{field}': {account}");
            }
        }
    }

    [Fact]
    public async Task AccountsReturns500ProblemJsonOnDatabaseFailure()
    {
        var response = await AccountsAgainstUnreachableDatabase();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task AccountsErrorBodyLeaksNoInternals()
    {
        var response = await AccountsAgainstUnreachableDatabase();
        var body = await response.Content.ReadAsStringAsync(Ct);

        // A stack frame, an exception type and the connection string's pieces are
        // each a separate way to leak; one assertion per way.
        Assert.DoesNotContain("   at ", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Npgsql", body, StringComparison.Ordinal);
        Assert.DoesNotContain(UnreachablePassword, body, StringComparison.Ordinal);
        Assert.DoesNotContain("Host=", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AccountProviderPairIsUnique()
    {
        var connectionString = await FreshMigratedDatabaseAsync();
        await using var db = Db(connectionString);

        db.Accounts.Add(NewAccount("pluggy", "same-id"));
        await db.SaveChangesAsync(Ct);

        db.Accounts.Add(NewAccount("pluggy", "same-id"));
        await ConstraintAssert.ViolatesAsync(
            () => db.SaveChangesAsync(Ct),
            ConstraintAssert.UniqueViolation,
            "ix_accounts_provider_provider_account_id");
        db.ChangeTracker.Clear();

        // The same provider id under another provider is a different account: the
        // index is over the pair, not over provider_account_id alone.
        db.Accounts.Add(NewAccount("other-provider", "same-id"));
        await db.SaveChangesAsync(Ct);

        Assert.Equal(2, await db.Accounts.CountAsync(Ct));
    }

    private const string UnreachablePassword = "unreachable-db-password";

    private static async Task<HttpResponseMessage> AccountsAgainstUnreachableDatabase()
    {
        await using var factory = new ApiFactory(
            $"Host=127.0.0.1;Port=1;Database=imoney;Username=imoney;Password={UnreachablePassword};Timeout=2;Command Timeout=2",
            environment: "Production",
            sharedToken: Token);
        var client = Authorized(factory);

        return await client.GetAsync("/v1/accounts", Ct);
    }

    private static HttpClient Authorized(ApiFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return client;
    }

    private static Account NewAccount(string provider, string providerAccountId) => new()
    {
        Id = Guid.NewGuid(),
        Provider = provider,
        ProviderAccountId = providerAccountId,
        Kind = AccountKind.CHECKING,
        Name = "Conta",
        Balance = 1m,
    };

    private static AppDbContext Db(string connectionString) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options);

    private async Task<string> FreshMigratedDatabaseAsync()
    {
        var database = $"acc_{Guid.NewGuid():N}";
        await postgres.CreateDatabaseAsync(database);
        var connectionString = postgres.ConnectionStringForFreshDatabase(database);

        await using var db = Db(connectionString);
        await db.Database.MigrateAsync(Ct);
        return connectionString;
    }

    private async Task<string> SeededDatabaseAsync()
    {
        var connectionString = await FreshMigratedDatabaseAsync();
        await using var db = Db(connectionString);
        await Seeder.RunAsync(db, Ct);
        return connectionString;
    }

    private static async Task<int> SeededAccountCountAsync(string connectionString)
    {
        await using var db = Db(connectionString);
        return await db.Accounts.CountAsync(Ct);
    }
}
