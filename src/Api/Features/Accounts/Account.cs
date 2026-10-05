namespace Api.Features.Accounts;

/// <summary>A normalized bank account: either a checking account or a card.</summary>
public sealed class Account
{
    public Guid Id { get; set; }

    /// <summary>The aggregator this account came from, e.g. "pluggy".</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>
    /// The account's id at the provider. Together with <see cref="Provider"/> this
    /// is the idempotency key the aggregator ingestion reconciles on.
    /// </summary>
    public string ProviderAccountId { get; set; } = string.Empty;

    public AccountKind Kind { get; set; }

    public string Name { get; set; } = string.Empty;

    public decimal Balance { get; set; }
}
