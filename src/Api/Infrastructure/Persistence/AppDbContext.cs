using Api.Features.Accounts;
using Api.Features.Categories;
using Api.Features.Transactions;
using Microsoft.EntityFrameworkCore;

namespace Api.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Account> Accounts => Set<Account>();

    public DbSet<Transaction> Transactions => Set<Transaction>();

    public DbSet<Category> Categories => Set<Category>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.Entity<Account>(account =>
        {
            account.ToTable("accounts");
            account.HasKey(a => a.Id);
            account.Property(a => a.Id).HasColumnName("id");
            account.Property(a => a.Provider).HasColumnName("provider").IsRequired();
            account.Property(a => a.ProviderAccountId).HasColumnName("provider_account_id").IsRequired();
            account.Property(a => a.Name).HasColumnName("name").IsRequired();
            account.Property(a => a.Balance).HasColumnName("balance").HasColumnType("numeric(18,2)");
            account.Property(a => a.Kind)
                .HasColumnName("kind")
                .HasConversion<string>()
                .IsRequired();

            // Door 1: the aggregator's idempotency key. Adding this after rows exist
            // would need a backfill migration; with the table empty it costs one line.
            account.HasIndex(a => new { a.Provider, a.ProviderAccountId })
                .IsUnique()
                .HasDatabaseName("ix_accounts_provider_provider_account_id");

            account.ToTable(t => t.HasCheckConstraint(
                "ck_accounts_kind",
                "kind IN ('CHECKING', 'CREDIT_CARD')"));
        });

        builder.Entity<Transaction>(transaction =>
        {
            transaction.ToTable("transactions");
            transaction.HasKey(t => t.Id);
            transaction.Property(t => t.Id).HasColumnName("id");
            transaction.Property(t => t.AccountId).HasColumnName("account_id").IsRequired();
            transaction.Property(t => t.Provider).HasColumnName("provider").IsRequired();
            transaction.Property(t => t.ProviderTransactionId).HasColumnName("provider_transaction_id").IsRequired();
            transaction.Property(t => t.Amount).HasColumnName("amount").HasColumnType("numeric(18,2)");
            transaction.Property(t => t.OccurredAt).HasColumnName("occurred_at").IsRequired();
            transaction.Property(t => t.CategoryId).HasColumnName("category_id");
            transaction.Property(t => t.Type)
                .HasColumnName("type")
                .HasConversion<string>()
                .IsRequired();
            transaction.Property(t => t.Status)
                .HasColumnName("status")
                .HasConversion<string>()
                .IsRequired();

            // Door 1, same argument as accounts.
            transaction.HasIndex(t => new { t.Provider, t.ProviderTransactionId })
                .IsUnique()
                .HasDatabaseName("ix_transactions_provider_provider_transaction_id");

            // Door 2: the paging order is (occurred_at desc, id desc), so the index
            // that serves it is part of the contract, not an optimization.
            transaction.HasIndex(t => new { t.OccurredAt, t.Id })
                .HasDatabaseName("ix_transactions_occurred_at_id");

            // Door 3: the closed enums, enforced by the database rather than only by
            // the C# type, so a direct INSERT cannot widen them either.
            transaction.ToTable(t => t.HasCheckConstraint(
                "ck_transactions_type",
                "type IN ('PIX_SENT', 'PIX_RECEIVED', 'DEBIT', 'CREDIT')"));
            transaction.ToTable(t => t.HasCheckConstraint(
                "ck_transactions_status",
                "status IN ('PENDING', 'POSTED')"));

            transaction.HasOne(t => t.Account)
                .WithMany()
                .HasForeignKey(t => t.AccountId)
                .OnDelete(DeleteBehavior.Cascade);

            // Optional by design: a transaction with no category is valid, one
            // pointing at a category that does not exist is not.
            transaction.HasOne(t => t.Category)
                .WithMany()
                .HasForeignKey(t => t.CategoryId)
                .IsRequired(false)
                .OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<Category>(category =>
        {
            category.ToTable("categories");
            category.HasKey(c => c.Id);
            category.Property(c => c.Id).HasColumnName("id");
            category.Property(c => c.Name).HasColumnName("name").IsRequired();
        });
    }
}
