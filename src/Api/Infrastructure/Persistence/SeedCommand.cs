using Microsoft.EntityFrameworkCore;

namespace Api.Infrastructure.Persistence;

/// <summary>The `seed` entry point: applies migrations, then inserts the synthetic set.</summary>
public static class SeedCommand
{
    public static async Task<int> RunAsync(IServiceProvider services, ILogger logger)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        try
        {
            await db.Database.MigrateAsync();
            var (accounts, transactions, categories) = await Seeder.RunAsync(db);

            logger.LogInformation(
                "seed: added {Accounts} accounts, {Transactions} transactions, {Categories} categories",
                accounts,
                transactions,
                categories);

            return 0;
        }
        catch (Exception ex)
        {
            // The message is what AC 8 asks for, but it travels through the redactor
            // first: an Npgsql failure can quote the connection string it tried, and
            // the constitution bars a secret from reaching any output.
            var safe = ConnectionSecretRedactor.Redact(ex.Message, db.Database.GetConnectionString());
            logger.LogError("seed: could not reach the database: {Error}", safe);
            await Console.Error.WriteLineAsync($"seed: could not reach the database: {safe}");

            return 1;
        }
    }
}
