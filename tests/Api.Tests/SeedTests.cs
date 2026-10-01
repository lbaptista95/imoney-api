using Api.Features.Transactions;
using Api.Infrastructure.Persistence;
using Api.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Api.Tests;

/// <summary>S1: the synthetic seed (C7-C11).</summary>
[Collection(PostgresCollection.Name)]
public sealed class SeedTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SeedInsertsFourAccountsAcrossBothProviders()
    {
        await using var db = await SeededDatabaseAsync();

        var accounts = await db.Accounts.ToListAsync(Ct);

        Assert.True(accounts.Count >= 4, $"expected at least 4 accounts, found {accounts.Count}");

        // Named one by one rather than counted: a count of four passes with four
        // Itaú checking accounts, which is not what AC 6 asks for.
        Assert.Contains(accounts, a => a.Name.Contains("Itaú") && a.Kind == Features.Accounts.AccountKind.CHECKING);
        Assert.Contains(accounts, a => a.Name.Contains("Itaú") && a.Kind == Features.Accounts.AccountKind.CREDIT_CARD);
        Assert.Contains(accounts, a => a.Name.Contains("Nubank") && a.Kind == Features.Accounts.AccountKind.CHECKING);
        Assert.Contains(accounts, a => a.Name.Contains("Nubank") && a.Kind == Features.Accounts.AccountKind.CREDIT_CARD);
    }

    [Fact]
    public async Task SeedInserts120TransactionsAcrossAllTypesAnd60Days()
    {
        await using var db = await SeededDatabaseAsync();

        var transactions = await db.Transactions.ToListAsync(Ct);

        Assert.True(
            transactions.Count >= 120,
            $"expected at least 120 transactions, found {transactions.Count}");

        foreach (var type in Enum.GetValues<TransactionType>())
        {
            Assert.Contains(transactions, t => t.Type == type);
        }

        var span = transactions.Max(t => t.OccurredAt) - transactions.Min(t => t.OccurredAt);
        Assert.True(span.TotalDays >= 60, $"expected at least 60 days of history, found {span.TotalDays:F1}");
    }

    [Fact]
    public async Task SeedIsIdempotentOnSecondRun()
    {
        var connectionString = await FreshDatabaseAsync();

        await using var factory = new ApiFactory(connectionString);
        factory.CreateClient();

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await Seeder.RunAsync(db, Ct);
        var afterFirst = (await db.Accounts.CountAsync(Ct), await db.Transactions.CountAsync(Ct));

        db.ChangeTracker.Clear();
        await Seeder.RunAsync(db, Ct);
        var afterSecond = (await db.Accounts.CountAsync(Ct), await db.Transactions.CountAsync(Ct));

        Assert.Equal(afterFirst, afterSecond);
    }

    [Fact]
    public async Task SeedExitsNonZeroWhenDatabaseUnreachable()
    {
        var result = await SeedProcess.RunAsync(
            "Host=127.0.0.1;Port=1;Database=imoney;Username=imoney;Password=s3cr3t-not-real;Timeout=2");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("could not reach the database", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SeedNeverPrintsConnectionSecret()
    {
        const string password = "pw-that-must-not-appear";

        var failed = await SeedProcess.RunAsync(
            $"Host=127.0.0.1;Port=1;Database=imoney;Username=imoney;Password={password};Timeout=2");
        var succeeded = await SeedProcess.RunAsync(await FreshDatabaseAsync());

        // Both runs, because the failing one is the one that quotes the connection
        // string and the succeeding one is the one nobody thinks to check.
        Assert.DoesNotContain(password, failed.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(password, succeeded.Output, StringComparison.Ordinal);
        Assert.Equal(0, succeeded.ExitCode);
    }

    [Fact]
    public async Task DevelopmentBootFailsFastWhenDatabaseUnreachable()
    {
        const string password = "boot-password-that-must-not-appear";

        var result = await SeedProcess.BootAsync(
            $"Host=127.0.0.1;Port=1;Database=imoney;Username=imoney;Password={password};Timeout=2",
            timeout: TimeSpan.FromSeconds(60));

        // Exiting is the claim. Staying up - serving with no migration applied - is
        // the failure, and it shows up as a timeout, not as an exit code.
        Assert.False(result.TimedOut, $"the API kept running with no reachable database:\n{result.Output}");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("could not reach the database", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(password, result.Output, StringComparison.Ordinal);
    }

    private async Task<string> FreshDatabaseAsync()
    {
        var database = $"seed_{Guid.NewGuid():N}";
        await postgres.CreateDatabaseAsync(database);
        return postgres.ConnectionStringForFreshDatabase(database);
    }

    private async Task<AppDbContext> SeededDatabaseAsync()
    {
        var connectionString = await FreshDatabaseAsync();
        var result = await SeedProcess.RunAsync(connectionString);
        Assert.Equal(0, result.ExitCode);

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        return new AppDbContext(options);
    }
}
