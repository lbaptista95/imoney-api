using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Api.Features.Accounts;
using Api.Features.Transactions;
using Api.Infrastructure.Persistence;
using Api.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace Api.Tests;

/// <summary>S4: GET /v1/transactions pages by cursor (C26-C37, C52).</summary>
[Collection(PostgresCollection.Name)]
public sealed class TransactionsTests(PostgresFixture postgres)
{
    private const string Token = "transactions-test-token";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TransactionsFirstPageIs50InStableDescendingOrder()
    {
        // Ties on occurred_at are built in, so the id tiebreak has something to decide.
        var connectionString = await DatabaseWithAsync(120, tiesEvery: 3);
        await using var factory = new ApiFactory(connectionString, sharedToken: Token);

        var page = await GetPageAsync(Authorized(factory), "/v1/transactions");

        Assert.Equal(50, page.Items.Count);

        for (var i = 1; i < page.Items.Count; i++)
        {
            var previous = page.Items[i - 1];
            var current = page.Items[i];

            Assert.True(
                previous.OccurredAt > current.OccurredAt
                || (previous.OccurredAt == current.OccurredAt && previous.Id.CompareTo(current.Id) > 0),
                $"row {i} breaks the (occurred_at desc, id desc) order: {previous} then {current}");
        }
    }

    [Fact]
    public async Task NextCursorPresentOnlyWhileMorePagesRemain()
    {
        var connectionString = await DatabaseWithAsync(120);
        await using var factory = new ApiFactory(connectionString, sharedToken: Token);
        var client = Authorized(factory);

        var pages = await WalkAsync(client, limit: 50);

        Assert.Equal(3, pages.Count);
        Assert.NotNull(pages[0].NextCursor);
        Assert.NotNull(pages[1].NextCursor);
        Assert.Null(pages[2].NextCursor);
    }

    [Fact]
    public async Task CursorWalkCoversEveryRowExactlyOnce()
    {
        var connectionString = await DatabaseWithAsync(120);
        await using var factory = new ApiFactory(connectionString, sharedToken: Token);

        var pages = await WalkAsync(Authorized(factory), limit: 50);

        Assert.Equal([50, 50, 20], pages.Select(p => p.Items.Count));

        var ids = pages.SelectMany(p => p.Items).Select(t => t.Id).ToList();
        Assert.Equal(120, ids.Count);
        Assert.Equal(120, ids.Distinct().Count());
        Assert.Equal((await AllIdsAsync(connectionString)).Order(), ids.Order());
    }

    [Fact]
    public async Task CursorWalkIsStableAcrossTiedOccurredAt()
    {
        // Every row shares one of a handful of timestamps, so page boundaries are
        // guaranteed to land inside a tie. A cursor over occurred_at alone would skip
        // or repeat rows here; only the (occurred_at, id) pair survives it.
        var connectionString = await DatabaseWithAsync(120, tiesEvery: 30);
        await using var factory = new ApiFactory(connectionString, sharedToken: Token);

        var pages = await WalkAsync(Authorized(factory), limit: 50);

        var ids = pages.SelectMany(p => p.Items).Select(t => t.Id).ToList();
        Assert.Equal(120, ids.Count);
        Assert.Equal(120, ids.Distinct().Count());
    }

    [Fact]
    public async Task LimitAbove200IsClampedTo200()
    {
        var connectionString = await DatabaseWithAsync(250);
        await using var factory = new ApiFactory(connectionString, sharedToken: Token);
        var client = Authorized(factory);

        var response = await client.GetAsync("/v1/transactions?limit=201", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(200, (await PageFromAsync(response)).Items.Count);
    }

    [Theory]
    [InlineData("?limit=1", 1)]
    [InlineData("?limit=200", 200)]
    [InlineData("", 50)]
    public async Task LimitEdgesReturnExpectedPageSizes(string query, int expected)
    {
        var connectionString = await DatabaseWithAsync(250);
        await using var factory = new ApiFactory(connectionString, sharedToken: Token);
        var client = Authorized(factory);

        var response = await client.GetAsync($"/v1/transactions{query}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expected, (await PageFromAsync(response)).Items.Count);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public async Task NonPositiveLimitReturns400NamingLimit(string limit)
    {
        var connectionString = await DatabaseWithAsync(5);
        await using var factory = new ApiFactory(connectionString, sharedToken: Token);

        var response = await Authorized(factory).GetAsync($"/v1/transactions?limit={limit}", Ct);

        await AssertProblemNamingAsync(response, HttpStatusCode.BadRequest, "limit");
    }

    [Theory]
    [InlineData("not base64 at all!!")]
    [InlineData("aGVsbG8gd29ybGQ=")] // base64 of "hello world": valid encoding, wrong payload
    [InlineData("TRUNCATED")]
    public async Task UndecodableCursorReturns400NamingCursor(string cursor)
    {
        var connectionString = await DatabaseWithAsync(5);
        await using var factory = new ApiFactory(connectionString, sharedToken: Token);
        var client = Authorized(factory);

        // The truncated case is built from a real cursor, cut in half, so it is a
        // cursor this API issued that no longer decodes - not an arbitrary string.
        if (cursor == "TRUNCATED")
        {
            var real = (await GetPageAsync(client, "/v1/transactions?limit=1")).NextCursor!;
            cursor = real[..(real.Length / 2)];
        }

        var response = await client.GetAsync(
            $"/v1/transactions?cursor={Uri.EscapeDataString(cursor)}", Ct);

        await AssertProblemNamingAsync(response, HttpStatusCode.BadRequest, "cursor");
    }

    [Fact]
    public async Task EveryTransactionTypeIsOneOfTheClosedEnum()
    {
        var connectionString = await DatabaseWithAsync(120);
        await using var factory = new ApiFactory(connectionString, sharedToken: Token);

        var pages = await WalkAsync(Authorized(factory), limit: 50);
        var types = pages.SelectMany(p => p.Items).Select(t => t.Type).ToList();

        string[] closed = ["PIX_SENT", "PIX_RECEIVED", "DEBIT", "CREDIT"];
        Assert.All(types, type => Assert.Contains(type, closed));

        // And all four actually appear: a response that always said DEBIT would pass
        // the first assertion and still be wrong.
        Assert.Equal(closed.Order(), types.Distinct().Order());
    }

    [Fact]
    public async Task TransactionEnumsAreEnforcedByCheckConstraint()
    {
        var connectionString = await DatabaseWithAsync(1);
        var accountId = (await AnyAccountIdAsync(connectionString)).ToString();

        // Raw SQL, because the C# enum would refuse these values before the database
        // ever saw them - and the claim is about the database. The valid insert first
        // proves the statement itself is sound, so a failure below is the constraint.
        await InsertRawAsync(connectionString, accountId, type: "DEBIT", status: "POSTED");

        await ConstraintAssert.ViolatesAsync(
            () => InsertRawAsync(connectionString, accountId, type: "FOO", status: "POSTED"),
            ConstraintAssert.CheckViolation,
            "ck_transactions_type");
        await ConstraintAssert.ViolatesAsync(
            () => InsertRawAsync(connectionString, accountId, type: "DEBIT", status: "X"),
            ConstraintAssert.CheckViolation,
            "ck_transactions_status");
    }

    [Fact]
    public async Task TransactionProviderPairIsUnique()
    {
        var connectionString = await DatabaseWithAsync(0);
        await using var db = Db(connectionString);
        var account = await NewAccountAsync(db);

        db.Transactions.Add(NewTransaction(account.Id, "pluggy", "same-id", DateTimeOffset.UtcNow, 1m));
        await db.SaveChangesAsync(Ct);

        db.Transactions.Add(NewTransaction(account.Id, "pluggy", "same-id", DateTimeOffset.UtcNow, 2m));
        await ConstraintAssert.ViolatesAsync(
            () => db.SaveChangesAsync(Ct),
            ConstraintAssert.UniqueViolation,
            "ix_transactions_provider_provider_transaction_id");
        db.ChangeTracker.Clear();

        db.Transactions.Add(NewTransaction(account.Id, "other-provider", "same-id", DateTimeOffset.UtcNow, 3m));
        await db.SaveChangesAsync(Ct);

        Assert.Equal(2, await db.Transactions.CountAsync(Ct));
    }

    [Fact]
    public async Task NoLogLineCarriesTransactionValueOrDescription()
    {
        // Amounts chosen to be unmistakable in a log line. This schema has no
        // description, Pix counterparty or document column yet, so `amount` is the
        // only one of the four the constitution names that can leak here; the claim
        // grows with the schema, through this same test.
        decimal[] sentinels = [98765.43m, 87654.31m, 76543.21m];
        var connectionString = await DatabaseWithAsync(0);

        await using (var db = Db(connectionString))
        {
            var account = await NewAccountAsync(db);
            var at = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
            for (var i = 0; i < sentinels.Length; i++)
            {
                db.Transactions.Add(NewTransaction(account.Id, "test", $"s-{i}", at.AddDays(-i), sentinels[i]));
            }

            await db.SaveChangesAsync(Ct);
        }

        await using var factory = new ApiFactory(connectionString, sharedToken: Token);
        var pages = await WalkAsync(Authorized(factory), limit: 1);
        Assert.Equal(3, pages.Count);

        var lines = factory.LogLines;

        // The positive control: the capture must reach below Information, or "no line
        // carries an amount" is only true of the lines nobody looked at. Without this,
        // an amount logged at Debug passed this test - which is how it was found.
        Assert.Contains(lines, line => line.StartsWith("Debug ", StringComparison.Ordinal)
            || line.StartsWith("Trace ", StringComparison.Ordinal));

        foreach (var amount in sentinels)
        {
            // Both cultures, because a log formatted in pt-BR writes the comma form.
            foreach (var form in new[]
                     {
                         amount.ToString(CultureInfo.InvariantCulture),
                         amount.ToString(new CultureInfo("pt-BR")),
                     })
            {
                Assert.DoesNotContain(lines, line => line.Contains(form, StringComparison.Ordinal));
            }
        }
    }

    [Fact]
    public async Task TransactionsReturns500ProblemJsonOnDatabaseFailure()
    {
        const string password = "unreachable-transactions-db-password";
        await using var factory = new ApiFactory(
            $"Host=127.0.0.1;Port=1;Database=imoney;Username=imoney;Password={password};Timeout=2;Command Timeout=2",
            environment: "Production",
            sharedToken: Token);

        var response = await Authorized(factory).GetAsync("/v1/transactions", Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("   at ", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception", body, StringComparison.Ordinal);
        Assert.DoesNotContain(password, body, StringComparison.Ordinal);
        Assert.DoesNotContain("Host=", body, StringComparison.OrdinalIgnoreCase);
    }

    // ---- helpers -------------------------------------------------------------

    private sealed record Row(Guid Id, string Type, DateTimeOffset OccurredAt);

    private sealed record Page(IReadOnlyList<Row> Items, string? NextCursor);

    private static HttpClient Authorized(ApiFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return client;
    }

    private static async Task<Page> GetPageAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await PageFromAsync(response);
    }

    private static async Task<Page> PageFromAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var root = body.RootElement;

        // Read by the wire names the plan's Surface fixes, so a rename is a failure here.
        var items = root.GetProperty("items").EnumerateArray()
            .Select(item => new Row(
                item.GetProperty("id").GetGuid(),
                item.GetProperty("type").GetString()!,
                item.GetProperty("occurred_at").GetDateTimeOffset()))
            .ToList();

        var next = root.GetProperty("next_cursor");
        return new Page(items, next.ValueKind == JsonValueKind.Null ? null : next.GetString());
    }

    private static async Task<List<Page>> WalkAsync(HttpClient client, int limit)
    {
        var pages = new List<Page>();
        string? cursor = null;

        do
        {
            var url = $"/v1/transactions?limit={limit}"
                + (cursor is null ? string.Empty : $"&cursor={Uri.EscapeDataString(cursor)}");
            var page = await GetPageAsync(client, url);
            pages.Add(page);
            cursor = page.NextCursor;

            // A cursor that never ends would hang the suite instead of failing it.
            Assert.True(pages.Count <= 1000, "the cursor walk did not terminate");
        }
        while (cursor is not null);

        return pages;
    }

    private static async Task AssertProblemNamingAsync(
        HttpResponseMessage response,
        HttpStatusCode status,
        string field)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(field, body.RootElement.GetProperty("field").GetString());
    }

    /// <summary>
    /// A fresh, migrated database holding <paramref name="count"/> transactions over
    /// one account. With <paramref name="tiesEvery"/>, that many consecutive rows share
    /// one occurred_at, so the paging order has real ties to break.
    /// </summary>
    private async Task<string> DatabaseWithAsync(int count, int tiesEvery = 1)
    {
        var database = $"txn_{Guid.NewGuid():N}";
        await postgres.CreateDatabaseAsync(database);
        var connectionString = postgres.ConnectionStringForFreshDatabase(database);

        await using var db = Db(connectionString);
        await db.Database.MigrateAsync(Ct);

        var account = await NewAccountAsync(db);
        var newest = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
        var types = Enum.GetValues<TransactionType>();

        for (var i = 0; i < count; i++)
        {
            var transaction = NewTransaction(
                account.Id,
                "test",
                $"t-{i:D5}",
                newest.AddHours(-(i / tiesEvery)),
                10m + i);
            transaction.Type = types[i % types.Length];
            db.Transactions.Add(transaction);
        }

        await db.SaveChangesAsync(Ct);
        return connectionString;
    }

    private static async Task<Account> NewAccountAsync(AppDbContext db)
    {
        var account = new Account
        {
            Id = Guid.NewGuid(),
            Provider = "test",
            ProviderAccountId = $"acc-{Guid.NewGuid():N}",
            Kind = AccountKind.CHECKING,
            Name = "Conta",
            Balance = 0m,
        };
        db.Accounts.Add(account);
        await db.SaveChangesAsync(Ct);
        return account;
    }

    private static Transaction NewTransaction(
        Guid accountId,
        string provider,
        string providerTransactionId,
        DateTimeOffset occurredAt,
        decimal amount) => new()
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Provider = provider,
            ProviderTransactionId = providerTransactionId,
            Type = TransactionType.DEBIT,
            Amount = amount,
            OccurredAt = occurredAt,
            Status = TransactionStatus.POSTED,
        };

    private static AppDbContext Db(string connectionString) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options);

    private static async Task<List<Guid>> AllIdsAsync(string connectionString)
    {
        await using var db = Db(connectionString);
        return await db.Transactions.Select(t => t.Id).ToListAsync(Ct);
    }

    private static async Task<Guid> AnyAccountIdAsync(string connectionString)
    {
        await using var db = Db(connectionString);
        return await db.Accounts.Select(a => a.Id).FirstAsync(Ct);
    }

    private static async Task InsertRawAsync(string connectionString, string accountId, string type, string status)
    {
        await using var connection = new Npgsql.NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO transactions
                (id, account_id, provider, provider_transaction_id, type, amount, occurred_at, status)
            VALUES
                (gen_random_uuid(), @account::uuid, 'raw', @ptid, @type, 1, now(), @status)
            """;
        command.Parameters.AddWithValue("account", accountId);
        command.Parameters.AddWithValue("ptid", Guid.NewGuid().ToString());
        command.Parameters.AddWithValue("type", type);
        command.Parameters.AddWithValue("status", status);
        await command.ExecuteNonQueryAsync(Ct);
    }
}
