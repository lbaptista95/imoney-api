using Api.Infrastructure.Persistence;
using Api.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Api.Tests;

/// <summary>
/// C66: every NOT NULL column of the schema, table-driven. A check constraint accepts
/// NULL, so the enums of door 3 are only closed if the columns are NOT NULL too - and
/// nothing proved that (F2 of the second verification), nor account_id (F3), nor the
/// other required columns (Q-a).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SchemaTests(PostgresFixture postgres)
{
    private const string NotNullViolation = "23502";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The required columns, written out. Comparing the database against this list is
    /// what makes a migration that drops or adds a NOT NULL fail here until the list -
    /// and so the decision - is updated on purpose.
    /// </summary>
    private static readonly string[] ExpectedNotNull =
    [
        "accounts.id",
        "accounts.provider",
        "accounts.provider_account_id",
        "accounts.kind",
        "accounts.name",
        "accounts.balance",
        "categories.id",
        "categories.name",
        "transactions.id",
        "transactions.account_id",
        "transactions.provider",
        "transactions.provider_transaction_id",
        "transactions.type",
        "transactions.amount",
        "transactions.occurred_at",
        "transactions.status",
    ];

    [Fact]
    public async Task EveryNotNullColumnRejectsNull()
    {
        var connectionString = await MigratedDatabaseAsync();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);

        var actual = await NotNullColumnsAsync(connection);
        Assert.Equal(ExpectedNotNull.Order(), actual.Order());

        var accountId = Guid.NewGuid();

        // The positive controls: a full valid row in every table, so a failure below can
        // only be the NULL and never a statement that was broken to begin with.
        await InsertAsync(connection, "accounts", ValidRow("accounts", accountId, id: accountId));
        await InsertAsync(connection, "categories", ValidRow("categories", accountId));
        await InsertAsync(connection, "transactions", ValidRow("transactions", accountId));

        foreach (var qualified in ExpectedNotNull)
        {
            var (table, column) = (qualified.Split('.')[0], qualified.Split('.')[1]);
            var row = ValidRow(table, accountId);
            row[column] = "NULL";

            var error = await Assert.ThrowsAsync<PostgresException>(() => InsertAsync(connection, table, row));
            Assert.True(
                error.SqlState == NotNullViolation && error.ColumnName == column,
                $"NULL in {qualified} failed as {error.SqlState} on '{error.ColumnName}', not as a NOT NULL violation of that column");
        }
    }

    /// <summary>
    /// SQL literals for one valid row. Every unique value is fresh, so the only thing a
    /// single row can violate is the NULL the test puts in.
    /// </summary>
    private static Dictionary<string, string> ValidRow(string table, Guid accountId, Guid? id = null)
    {
        var rowId = $"'{id ?? Guid.NewGuid()}'::uuid";
        var unique = $"'{Guid.NewGuid():N}'";

        return table switch
        {
            "accounts" => new()
            {
                ["id"] = rowId,
                ["provider"] = "'schema-test'",
                ["provider_account_id"] = unique,
                ["kind"] = "'CHECKING'",
                ["name"] = "'Conta'",
                ["balance"] = "0",
            },
            "categories" => new()
            {
                ["id"] = rowId,
                ["name"] = "'Mercado'",
            },
            "transactions" => new()
            {
                ["id"] = rowId,
                ["account_id"] = $"'{accountId}'::uuid",
                ["provider"] = "'schema-test'",
                ["provider_transaction_id"] = unique,
                ["type"] = "'DEBIT'",
                ["amount"] = "1",
                ["occurred_at"] = "now()",
                ["status"] = "'POSTED'",
            },
            _ => throw new ArgumentOutOfRangeException(nameof(table), table, "no valid row for this table"),
        };
    }

    private static async Task InsertAsync(NpgsqlConnection connection, string table, Dictionary<string, string> row)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"INSERT INTO {table} ({string.Join(", ", row.Keys)}) VALUES ({string.Join(", ", row.Values)})";
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<List<string>> NotNullColumnsAsync(NpgsqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT table_name || '.' || column_name
            FROM information_schema.columns
            WHERE table_schema = 'public'
              AND table_name IN ('accounts', 'categories', 'transactions')
              AND is_nullable = 'NO'
            """;

        var columns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            columns.Add(reader.GetString(0));
        }

        return columns;
    }

    private async Task<string> MigratedDatabaseAsync()
    {
        var database = $"schema_{Guid.NewGuid():N}";
        await postgres.CreateDatabaseAsync(database);
        var connectionString = postgres.ConnectionStringForFreshDatabase(database);

        await using var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options);
        await db.Database.MigrateAsync(Ct);
        return connectionString;
    }
}
