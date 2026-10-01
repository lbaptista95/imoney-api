namespace Api.Features.Transactions;

/// <summary>
/// The closed set of transaction statuses (door 3). The seed writes the final
/// value; this feature has no write endpoint and no transition between them.
/// </summary>
public enum TransactionStatus
{
    PENDING,
    POSTED,
}
