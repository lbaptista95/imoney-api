using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Api.Infrastructure.Persistence;
using Api.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace Api.Tests;

/// <summary>
/// C70: the class "a field is present, but nobody checks what it holds". Three
/// verifications found instances of it one at a time - account balances, then four
/// transaction fields, then type and occurred_at. This proves the class: for every
/// route, every property the contract's response schema declares is compared with its
/// source on every row, and the set of compared properties must equal the schema's.
/// A field added to the contract without a comparison fails here.
/// </summary>
/// <remarks>
/// In the contract-file collection because it reads contracts/openapi.json, which C41
/// tampers with and restores.
/// </remarks>
[Collection(ContractFileCollection.Name)]
public sealed class ResponseFieldTests(PostgresFixture postgres)
{
    private const string Token = "response-field-token";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EveryResponseFieldMatchesItsSource()
    {
        var contract = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(ProcessRunner.RepoRoot, "contracts", "openapi.json"), Ct));

        var connectionString = await SeededDatabaseAsync();
        await using var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options);
        await using var factory = new ApiFactory(connectionString, sharedToken: Token);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        // ---- GET /v1/transactions: every row, against the database -----------
        var transactions = await db.Transactions.AsNoTracking().ToDictionaryAsync(t => t.Id, Ct);
        var transactionFields = new Dictionary<string, Action<JsonElement, Features.Transactions.Transaction>>
        {
            ["id"] = (json, row) => Assert.Equal(row.Id, json.GetGuid()),
            ["account_id"] = (json, row) => Assert.Equal(row.AccountId, json.GetGuid()),
            ["type"] = (json, row) => Assert.Equal(row.Type.ToString(), json.GetString()),
            ["amount"] = (json, row) => Assert.Equal(row.Amount, json.GetDecimal()),
            ["occurred_at"] = (json, row) => Assert.Equal(row.OccurredAt, json.GetDateTimeOffset()),
            ["status"] = (json, row) => Assert.Equal(row.Status.ToString(), json.GetString()),
            ["category_id"] = (json, row) =>
            {
                if (row.CategoryId is null)
                {
                    Assert.Equal(JsonValueKind.Null, json.ValueKind);
                }
                else
                {
                    Assert.Equal(row.CategoryId, json.GetGuid());
                }
            },
        };
        Assert.Equal(
            SchemaProperties(contract, "/v1/transactions", itemsOf: "items").Order(),
            transactionFields.Keys.Order());

        var page = await GetJsonAsync(client, "/v1/transactions?limit=200");
        var items = page.RootElement.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(transactions.Count, items.Count);
        AssertEveryField(items, transactionFields, json => transactions[json.GetProperty("id").GetGuid()]);

        // ---- GET /v1/accounts: every row, against the database ---------------
        var accounts = await db.Accounts.AsNoTracking().ToDictionaryAsync(a => a.Id, Ct);
        var accountFields = new Dictionary<string, Action<JsonElement, Features.Accounts.Account>>
        {
            ["id"] = (json, row) => Assert.Equal(row.Id, json.GetGuid()),
            ["provider"] = (json, row) => Assert.Equal(row.Provider, json.GetString()),
            ["kind"] = (json, row) => Assert.Equal(row.Kind.ToString(), json.GetString()),
            ["name"] = (json, row) => Assert.Equal(row.Name, json.GetString()),
            ["balance"] = (json, row) => Assert.Equal(row.Balance, json.GetDecimal()),
        };
        Assert.Equal(SchemaProperties(contract, "/v1/accounts").Order(), accountFields.Keys.Order());

        var accountRows = (await GetJsonAsync(client, "/v1/accounts")).RootElement.EnumerateArray().ToList();
        Assert.Equal(accounts.Count, accountRows.Count);
        AssertEveryField(accountRows, accountFields, json => accounts[json.GetProperty("id").GetGuid()]);

        // ---- GET /health: its one field, against its only healthy value -------
        var healthFields = new Dictionary<string, Action<JsonElement, string>>
        {
            ["status"] = (json, expected) => Assert.Equal(expected, json.GetString()),
        };
        Assert.Equal(SchemaProperties(contract, "/health").Order(), healthFields.Keys.Order());

        var health = (await GetJsonAsync(client, "/health")).RootElement;
        AssertEveryField([health], healthFields, _ => "healthy");
    }

    private static void AssertEveryField<TSource>(
        IEnumerable<JsonElement> rows,
        Dictionary<string, Action<JsonElement, TSource>> fields,
        Func<JsonElement, TSource> sourceOf)
    {
        foreach (var row in rows)
        {
            var source = sourceOf(row);
            foreach (var (name, compare) in fields)
            {
                Assert.True(row.TryGetProperty(name, out var value), $"'{name}' is missing from {row}");
                compare(value, source);
            }
        }
    }

    /// <summary>
    /// The property names of a route's 200 response schema in the committed contract,
    /// following $ref, through an array, and - for a page - into the named list.
    /// </summary>
    private static IReadOnlyList<string> SchemaProperties(JsonDocument contract, string path, string? itemsOf = null)
    {
        var schema = contract.RootElement
            .GetProperty("paths").GetProperty(path).GetProperty("get")
            .GetProperty("responses").GetProperty("200")
            .GetProperty("content").GetProperty("application/json").GetProperty("schema");

        schema = Resolve(contract, schema);
        if (schema.TryGetProperty("items", out var arrayItems) && !schema.TryGetProperty("properties", out _))
        {
            schema = Resolve(contract, arrayItems);
        }

        if (itemsOf is not null)
        {
            var list = Resolve(contract, schema.GetProperty("properties").GetProperty(itemsOf));
            schema = Resolve(contract, list.GetProperty("items"));
        }

        return schema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToList();
    }

    private static JsonElement Resolve(JsonDocument contract, JsonElement schema)
    {
        if (!schema.TryGetProperty("$ref", out var reference))
        {
            return schema;
        }

        var name = reference.GetString()!.Split('/')[^1];
        return contract.RootElement.GetProperty("components").GetProperty("schemas").GetProperty(name);
    }

    private static async Task<JsonDocument> GetJsonAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
    }

    private async Task<string> SeededDatabaseAsync()
    {
        var database = $"fields_{Guid.NewGuid():N}";
        await postgres.CreateDatabaseAsync(database);
        var connectionString = postgres.ConnectionStringForFreshDatabase(database);

        await using var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options);
        await db.Database.MigrateAsync(Ct);
        await Seeder.RunAsync(db, Ct);
        return connectionString;
    }
}
