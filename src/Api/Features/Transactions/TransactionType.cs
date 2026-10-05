namespace Api.Features.Transactions;

/// <summary>
/// The closed set of transaction types (door 3 in the feature's plan). Closing it
/// now, while the table is empty, is what avoids a backfill migration later: a
/// NOT NULL column with a check constraint over a populated table needs one.
/// </summary>
public enum TransactionType
{
    PIX_SENT,
    PIX_RECEIVED,
    DEBIT,
    CREDIT,
}
