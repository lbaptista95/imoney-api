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
    /// Every constraint and unique index of the three tables, written out - the class
    /// behind the primary keys that had no proof (F8 of the third verification). The
    /// database's own list must equal this one, and each entry must reject its violation.
    /// </summary>
    private static readonly string[] ExpectedConstraints =
    [
        "PK_accounts",
        "PK_categories",
        "PK_transactions",
        "ix_accounts_provider_provider_account_id",
        "ix_transactions_provider_provider_transaction_id",
        "FK_transactions_accounts_account_id",
        "FK_transactions_categories_category_id",
        "ck_accounts_kind",
        "ck_transactions_type",
        "ck_transactions_status",
    ];

    private const string UniqueViolation = "23505";
    private const string ForeignKeyViolation = "23503";
    private const string CheckViolation = "23514";

    [Fact]
    public async Task EveryConstraintRejectsItsViolation()
    {
        var connectionString = await MigratedDatabaseAsync();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);

        Assert.Equal(ExpectedConstraints.Order(), (await ConstraintsAsync(connection)).Order());

        var accountId = Guid.NewGuid();
        await InsertAsync(connection, "accounts", ValidRow("accounts", accountId, id: accountId));

        // Each entry: the row to insert first (if any), the violating row, and the
        // SQLSTATE the violation must raise. Every name in the list has an entry, which
        // the loop below enforces.
        var duplicateAccountId = Guid.NewGuid();
        var duplicateCategoryId = Guid.NewGuid();
        var duplicateTransactionId = Guid.NewGuid();
        var violations = new Dictionary<string, (string Table, Dictionary<string, string>? First, Dictionary<string, string> Violating, string SqlState)>
        {
            ["PK_accounts"] = ("accounts",
                ValidRow("accounts", accountId, id: duplicateAccountId),
                ValidRow("accounts", accountId, id: duplicateAccountId),
                UniqueViolation),
            ["PK_categories"] = ("categories",
                ValidRow("categories", accountId, id: duplicateCategoryId),
                ValidRow("categories", accountId, id: duplicateCategoryId),
                UniqueViolation),
            ["PK_transactions"] = ("transactions",
                ValidRow("transactions", accountId, id: duplicateTransactionId),
                ValidRow("transactions", accountId, id: duplicateTransactionId),
                UniqueViolation),
            ["ix_accounts_provider_provider_account_id"] = ("accounts",
                With(ValidRow("accounts", accountId), "provider_account_id", "'same-account'"),
                With(ValidRow("accounts", accountId), "provider_account_id", "'same-account'"),
                UniqueViolation),
            ["ix_transactions_provider_provider_transaction_id"] = ("transactions",
                With(ValidRow("transactions", accountId), "provider_transaction_id", "'same-transaction'"),
                With(ValidRow("transactions", accountId), "provider_transaction_id", "'same-transaction'"),
                UniqueViolation),
            ["FK_transactions_accounts_account_id"] = ("transactions",
                null,
                With(ValidRow("transactions", accountId), "account_id", $"'{Guid.NewGuid()}'::uuid"),
                ForeignKeyViolation),
            ["FK_transactions_categories_category_id"] = ("transactions",
                null,
                With(ValidRow("transactions", accountId), "category_id", $"'{Guid.NewGuid()}'::uuid"),
                ForeignKeyViolation),
            ["ck_accounts_kind"] = ("accounts",
                null,
                With(ValidRow("accounts", accountId), "kind", "'SAVINGS'"),
                CheckViolation),
            ["ck_transactions_type"] = ("transactions",
                null,
                With(ValidRow("transactions", accountId), "type", "'FOO'"),
                CheckViolation),
            ["ck_transactions_status"] = ("transactions",
                null,
                With(ValidRow("transactions", accountId), "status", "'X'"),
                CheckViolation),
        };

        Assert.Equal(ExpectedConstraints.Order(), violations.Keys.Order());

        foreach (var (name, (table, first, violating, sqlState)) in violations)
        {
            if (first is not null)
            {
                // The positive control: the same shape of row is accepted once, so the
                // second insert fails only because it repeats it.
                await InsertAsync(connection, table, first);
            }

            var error = await Assert.ThrowsAsync<PostgresException>(() => InsertAsync(connection, table, violating));
            Assert.True(
                error.SqlState == sqlState && error.ConstraintName == name,
                $"violating {name} failed as {error.SqlState} on '{error.ConstraintName}'");
        }
    }

    private static Dictionary<string, string> With(Dictionary<string, string> row, string column, string literal)
    {
        row[column] = literal;
        return row;
    }

    private static async Task<List<string>> ConstraintsAsync(NpgsqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.conname
            FROM pg_constraint c
            JOIN pg_class t ON t.oid = c.conrelid
            JOIN pg_namespace n ON n.oid = t.relnamespace
            WHERE n.nspname = 'public'
              AND t.relname IN ('accounts', 'categories', 'transactions')
              AND c.contype IN ('p', 'u', 'f', 'c')
            UNION
            SELECT i.indexname
            FROM pg_indexes i
            WHERE i.schemaname = 'public'
              AND i.tablename IN ('accounts', 'categories', 'transactions')
              AND i.indexdef LIKE 'CREATE UNIQUE INDEX%'
              AND i.indexname NOT IN (SELECT conname FROM pg_constraint)
            """;

        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            names.Add(reader.GetString(0));
        }

        return names;
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
