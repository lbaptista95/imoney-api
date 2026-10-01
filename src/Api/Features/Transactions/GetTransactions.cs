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
public sealed record TransactionPage(IReadOnlyList<TransactionResponse> Items, string? NextCursor)
{
    /// <summary>
    /// Builds a page from up to <paramref name="pageSize"/> + 1 fetched rows: the extra
    /// row only says that a next page exists and is never returned. Its own method so the
    /// boundary - a last page that is exactly full - is proven where it is decided (C75).
    /// </summary>
    public static TransactionPage Assemble(IReadOnlyList<TransactionResponse> fetched, int pageSize)
    {
        var hasMore = fetched.Count > pageSize;
        var items = hasMore ? fetched.Take(pageSize).ToList() : fetched;
        var next = hasMore && items.Count > 0
            ? new TransactionCursor(items[^1].OccurredAt, items[^1].Id).Encode()
            : null;

        return new TransactionPage(items, next);
    }
}

/// <summary>
/// Turns the requested `limit` into a page size. Its own type so the decision table can
/// be proven where it is decided (C63), not only through HTTP.
/// </summary>
public static class PageLimit
{
    /// <summary>The page size used when the caller asks for none.</summary>
    public const int Default = 50;

    /// <summary>The largest page the API will serve; a bigger request is clamped to it.</summary>
    public const int Max = 200;

    /// <summary>
    /// False for zero or below, which is a 400. Above the maximum is clamped rather than
    /// rejected: AC 19 asks for the largest allowed page, not an error.
    /// </summary>
    public static bool TryResolve(int? requested, out int pageSize)
    {
        if (requested is <= 0)
        {
            pageSize = 0;
            return false;
        }

        pageSize = Math.Min(requested ?? Default, Max);
        return true;
    }
}

public static class GetTransactions
{
    public const int DefaultLimit = PageLimit.Default;

    /// <summary>Lists transactions, newest first, paginated by an opaque cursor.</summary>
    public static async Task<IResult> HandleAsync(
        AppDbContext db,
        CancellationToken cancellationToken,
        string? cursor = null,
        int? limit = null)
    {
        if (!PageLimit.TryResolve(limit, out var pageSize))
        {
            return Problems.BadRequest(
                "limit",
                $"'limit' must be greater than zero. Omit it for the default of {DefaultLimit}.");
        }

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

        return Results.Ok(TransactionPage.Assemble(rows, pageSize));
    }
}
