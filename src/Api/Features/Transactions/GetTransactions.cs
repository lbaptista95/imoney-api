using Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Api.Features.Transactions;

/// <summary>One transaction in the `GET /v1/transactions` page.</summary>
public sealed record TransactionResponse(
    Guid Id,
    Guid AccountId,
    string Type,
    decimal Amount,
    DateTimeOffset OccurredAt,
    string Status,
    Guid? CategoryId);

/// <summary>A page of transactions, newest first.</summary>
/// <param name="Items">The page's rows.</param>
/// <param name="NextCursor">An opaque cursor for the next page, or null on the last page.</param>
public sealed record TransactionPage(IReadOnlyList<TransactionResponse> Items, string? NextCursor);

public static class GetTransactions
{
    /// <summary>The page size used when the caller asks for none.</summary>
    public const int DefaultLimit = 50;

    /// <summary>The largest page the API will serve; a bigger request is clamped to it.</summary>
    public const int MaxLimit = 200;

    /// <summary>Lists transactions, newest first, paginated by an opaque cursor.</summary>
    public static async Task<IResult> HandleAsync(
        AppDbContext db,
        CancellationToken cancellationToken,
        string? cursor = null,
        int? limit = null)
    {
        if (limit is <= 0)
        {
            return Problems.BadRequest(
                "limit",
                $"'limit' must be greater than zero. Omit it for the default of {DefaultLimit}.");
        }

        // Clamped rather than rejected: AC 19 asks for the largest allowed page, not
        // an error, when the caller asks for more than the API will serve.
        var pageSize = Math.Min(limit ?? DefaultLimit, MaxLimit);

        TransactionCursor? decoded = null;
        if (!string.IsNullOrEmpty(cursor))
        {
            if (!TransactionCursor.TryDecode(cursor, out decoded))
            {
                return Problems.BadRequest("cursor", "'cursor' is not a cursor this API issued.");
            }
        }

        var query = db.Transactions.AsNoTracking();

        if (decoded is not null)
        {
            // Keyset paging over the same (occurred_at desc, id desc) order the first
            // page used, so a tie on occurred_at cannot drop or repeat a row.
            query = query.Where(t =>
                t.OccurredAt < decoded.OccurredAt
                || (t.OccurredAt == decoded.OccurredAt && t.Id.CompareTo(decoded.Id) < 0));
        }

        // One row beyond the page, to learn whether a next page exists without a
        // second count query.
        var rows = await query
            .OrderByDescending(t => t.OccurredAt)
            .ThenByDescending(t => t.Id)
            .Take(pageSize + 1)
            .Select(t => new TransactionResponse(
                t.Id,
                t.AccountId,
                t.Type.ToString(),
                t.Amount,
                t.OccurredAt,
                t.Status.ToString(),
                t.CategoryId))
            .ToListAsync(cancellationToken);

        var hasMore = rows.Count > pageSize;
        var items = hasMore ? rows[..pageSize] : rows;
        var next = hasMore && items.Count > 0
            ? new TransactionCursor(items[^1].OccurredAt, items[^1].Id).Encode()
            : null;

        return Results.Ok(new TransactionPage(items, next));
    }
}
