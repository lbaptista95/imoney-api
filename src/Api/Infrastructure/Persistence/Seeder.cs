using Api.Features.Accounts;
using Api.Features.Categories;
using Api.Features.Transactions;
using Microsoft.EntityFrameworkCore;

namespace Api.Infrastructure.Persistence;

/// <summary>
/// Synthetic development data. Every value here is invented: the constitution bars
/// real financial data from any seed, fixture, test or log, so this file is the
/// whole data source for local development and there is no sanitization step to
/// forget.
/// </summary>
public static class Seeder
{
    private const string Provider = "synthetic";

    /// <summary>Fixed so two runs produce byte-identical rows, which is what makes the seed idempotent.</summary>
    private const int Seed = 20261001;

    private static readonly (string ProviderId, string Name, AccountKind Kind, decimal Balance)[] AccountSpecs =
    [
        ("itau-checking-1", "Itaú Conta Corrente", AccountKind.CHECKING, 4_210.55m),
        ("itau-card-1", "Itaú Cartão", AccountKind.CREDIT_CARD, -1_320.40m),
        ("nubank-checking-1", "Nubank Conta", AccountKind.CHECKING, 8_735.12m),
        ("nubank-card-1", "Nubank Cartão", AccountKind.CREDIT_CARD, -2_047.89m),
    ];

    private static readonly string[] CategoryNames =
        ["Mercado", "Transporte", "Moradia", "Lazer", "Salário"];

    /// <summary>
    /// Inserts the synthetic set, skipping anything already present by its provider
    /// key. Returns the number of rows it added, so a caller can tell a first run
    /// from a repeat.
    /// </summary>
    public static async Task<(int Accounts, int Transactions, int Categories)> RunAsync(
        AppDbContext db,
        CancellationToken cancellationToken = default)
    {
        var addedCategories = await SeedCategoriesAsync(db, cancellationToken);
        var addedAccounts = await SeedAccountsAsync(db, cancellationToken);
        var addedTransactions = await SeedTransactionsAsync(db, cancellationToken);

        return (addedAccounts, addedTransactions, addedCategories);
    }

    private static async Task<int> SeedCategoriesAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var existing = await db.Categories.Select(c => c.Name).ToListAsync(cancellationToken);
        var added = 0;

        foreach (var name in CategoryNames.Where(n => !existing.Contains(n)))
        {
            db.Categories.Add(new Category { Id = DeterministicId("category", name), Name = name });
            added++;
        }

        await db.SaveChangesAsync(cancellationToken);
        return added;
    }

    private static async Task<int> SeedAccountsAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var existing = await db.Accounts
            .Where(a => a.Provider == Provider)
            .Select(a => a.ProviderAccountId)
            .ToListAsync(cancellationToken);

        var added = 0;

        foreach (var spec in AccountSpecs.Where(s => !existing.Contains(s.ProviderId)))
        {
            db.Accounts.Add(new Account
            {
                Id = DeterministicId("account", spec.ProviderId),
                Provider = Provider,
                ProviderAccountId = spec.ProviderId,
                Kind = spec.Kind,
                Name = spec.Name,
                Balance = spec.Balance,
            });
            added++;
        }

        await db.SaveChangesAsync(cancellationToken);
        return added;
    }

    private static async Task<int> SeedTransactionsAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var accounts = await db.Accounts
            .Where(a => a.Provider == Provider)
            .OrderBy(a => a.ProviderAccountId)
            .ToListAsync(cancellationToken);

        var existing = await db.Transactions
            .Where(t => t.Provider == Provider)
            .Select(t => t.ProviderTransactionId)
            .ToListAsync(cancellationToken);

        var categoryIds = await db.Categories
            .OrderBy(c => c.Name)
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);

        var types = Enum.GetValues<TransactionType>();
        var random = new Random(Seed);
        var added = 0;

        // 128 rows over 64 days: enough for three pages at the default limit of 50,
        // and every type appears because the first rows walk the enum in order.
        const int count = 128;
        const int spanInDays = 64;
        var newest = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

        for (var i = 0; i < count; i++)
        {
            var providerTransactionId = $"txn-{i:D4}";
            if (existing.Contains(providerTransactionId))
            {
                continue;
            }

            var account = accounts[i % accounts.Count];
            var type = types[i % types.Length];

            // Two rows share occurred_at exactly, so the paging order has a real tie
            // to break with id - the case C29 proves.
            var dayOffset = i * spanInDays / count;

            db.Transactions.Add(new Transaction
            {
                Id = DeterministicId("transaction", providerTransactionId),
                AccountId = account.Id,
                Provider = Provider,
                ProviderTransactionId = providerTransactionId,
                Type = type,
                Amount = Math.Round((decimal)(random.NextDouble() * 900 + 10), 2),
                OccurredAt = newest.AddDays(-dayOffset),
                Status = i % 7 == 0 ? TransactionStatus.PENDING : TransactionStatus.POSTED,
                CategoryId = i % 5 == 0 ? null : categoryIds[i % categoryIds.Count],
            });
            added++;
        }

        await db.SaveChangesAsync(cancellationToken);
        return added;
    }

    /// <summary>
    /// Derives a stable id from a name, so a re-run reproduces the same rows instead
    /// of inserting near-duplicates under fresh GUIDs.
    /// </summary>
    private static Guid DeterministicId(string kind, string key)
    {
        var bytes = System.Security.Cryptography.MD5.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{kind}:{key}:{Seed}"));
        return new Guid(bytes);
    }
}
