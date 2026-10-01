using Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Api.Features.Accounts;

/// <summary>What `GET /v1/accounts` returns for one account.</summary>
/// <param name="Id">The account's local id.</param>
/// <param name="Provider">The aggregator it came from.</param>
/// <param name="Kind">CHECKING or CREDIT_CARD.</param>
/// <param name="Name">The account's display name.</param>
/// <param name="Balance">The current balance.</param>
public sealed record AccountResponse(
    Guid Id,
    string Provider,
    string Kind,
    string Name,
    decimal Balance);

public static class GetAccounts
{
    /// <summary>Lists every account.</summary>
    /// <remarks>A named method, not a lambda: the OpenAPI generator reads XML comments only from methods.</remarks>
    public static async Task<IResult> HandleAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var accounts = await db.Accounts
            .OrderBy(a => a.Name)
            .Select(a => new AccountResponse(
                a.Id,
                a.Provider,
                a.Kind.ToString(),
                a.Name,
                a.Balance))
            .ToListAsync(cancellationToken);

        return Results.Ok(accounts);
    }
}
