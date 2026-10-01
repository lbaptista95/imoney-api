using Api.Features.Accounts;
using Api.Features.Categories;

namespace Api.Features.Transactions;

/// <summary>A normalized transaction: Pix, debit or credit.</summary>
public sealed class Transaction
{
    public Guid Id { get; set; }

    public Guid AccountId { get; set; }

    public Account? Account { get; set; }

    /// <summary>The aggregator this transaction came from.</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>
    /// The transaction's id at the provider. With <see cref="Provider"/> this is the
    /// unique index that makes ingestion idempotent (door 1).
    /// </summary>
    public string ProviderTransactionId { get; set; } = string.Empty;

    public TransactionType Type { get; set; }

    public decimal Amount { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    public TransactionStatus Status { get; set; }

    /// <summary>Optional: categorization is a later feature, this one only creates the table.</summary>
    public Guid? CategoryId { get; set; }

    public Category? Category { get; set; }
}
