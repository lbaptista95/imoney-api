using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Api.Tests.Support;

/// <summary>
/// Asserts that a write failed because of one specific constraint. "It threw" is
/// not enough: a typo in the SQL, a missing column or a different constraint would
/// throw too, and the test would pass for a reason that has nothing to do with the
/// claim. The SQLSTATE and the constraint name pin the reason.
/// </summary>
public static class ConstraintAssert
{
    public const string UniqueViolation = "23505";
    public const string ForeignKeyViolation = "23503";
    public const string CheckViolation = "23514";

    public static async Task ViolatesAsync(Func<Task> write, string sqlState, string constraint)
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(write);

        var postgres = error as PostgresException
            ?? (error as DbUpdateException)?.InnerException as PostgresException;

        Assert.True(postgres is not null, $"expected a PostgreSQL error, got: {error}");
        Assert.Equal(sqlState, postgres!.SqlState);
        Assert.Equal(constraint, postgres.ConstraintName);
    }
}
