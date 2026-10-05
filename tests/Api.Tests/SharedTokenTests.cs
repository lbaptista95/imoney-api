using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Headers;
using Api.Tests.Support;

namespace Api.Tests;

/// <summary>S2: every /v1 route needs the shared token; /health does not (C12-C21).</summary>
[Collection(PostgresCollection.Name)]
public sealed class SharedTokenTests(PostgresFixture postgres)
{
    private const string Token = "a-configured-shared-token";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AccountsWithoutAuthorizationHeaderReturns401()
        => Assert.Equal(HttpStatusCode.Unauthorized, await StatusOf("/v1/accounts", header: null));

    [Fact]
    public async Task TransactionsWithoutAuthorizationHeaderReturns401()
        => Assert.Equal(HttpStatusCode.Unauthorized, await StatusOf("/v1/transactions", header: null));

    [Fact]
    public async Task AccountsWithWrongTokenReturns401()
        => Assert.Equal(
            HttpStatusCode.Unauthorized,
            await StatusOf("/v1/accounts", new AuthenticationHeaderValue("Bearer", "a-different-token")));

    [Fact]
    public async Task TransactionsWithWrongTokenReturns401()
        => Assert.Equal(
            HttpStatusCode.Unauthorized,
            await StatusOf("/v1/transactions", new AuthenticationHeaderValue("Bearer", "a-different-token")));

    [Fact]
    public async Task TokenComparisonRejectsPrefixAndExtension()
    {
        // A comparison that stops at the shorter string accepts both of these. The
        // claim is that neither is accepted, which is what rules that out.
        var prefix = Token[..^1];
        var extension = Token + "x";

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            await StatusOf("/v1/accounts", new AuthenticationHeaderValue("Bearer", prefix)));
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            await StatusOf("/v1/accounts", new AuthenticationHeaderValue("Bearer", extension)));
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            await StatusOf("/v1/accounts", new AuthenticationHeaderValue("Bearer", string.Empty)));
    }

    /// <summary>
    /// Not a numbered check: it keeps the runtime honest to the contract, which
    /// documents every 401 as problem+json. The framework's default challenge sends an
    /// empty body, and nothing else here would notice a return to it.
    /// </summary>
    [Fact]
    public async Task UnauthorizedAnswersProblemJson()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString, sharedToken: Token);
        var client = factory.CreateClient();

        var response = await client.GetAsync("/v1/accounts", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task AccountsWithConfiguredTokenReturns200()
        => Assert.Equal(
            HttpStatusCode.OK,
            await StatusOf("/v1/accounts", new AuthenticationHeaderValue("Bearer", Token)));

    [Fact]
    public async Task HealthNeedsNoToken()
        => Assert.Equal(HttpStatusCode.OK, await StatusOf("/health", header: null));

    /// <summary>
    /// C78: the routes the server actually maps, read from its endpoint data source.
    /// C39 proves the generated document lists three routes; this proves the server
    /// serves no fourth one - which it did, an unauthenticated /openapi/v1.json from
    /// MapOpenApi, that no check could see.
    /// </summary>
    [Fact]
    public async Task ServerMapsExactlyTheSurfaceRoutes()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString, sharedToken: Token);
        factory.CreateClient();

        var routes = factory.Services
            .GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>()
            .Endpoints
            .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
            .SelectMany(endpoint =>
                (endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods ?? ["*"])
                .Select(method => $"{method} /{endpoint.RoutePattern.RawText?.TrimStart('/')}"))
            .Order()
            .ToList();

        Assert.Equal(["GET /health", "GET /v1/accounts", "GET /v1/transactions"], routes);
    }

    [Fact]
    public async Task StartupFailsNamingMissingSharedToken()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString, sharedToken: null);

        var error = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            var client = factory.CreateClient();
            await client.GetAsync("/health", Ct);
        });

        Assert.Contains("Auth:SharedToken", Flatten(error), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task StartupFailsOnBlankSharedToken(string configured)
    {
        await using var factory = new ApiFactory(postgres.ConnectionString, sharedToken: configured);

        var error = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            var client = factory.CreateClient();
            await client.GetAsync("/health", Ct);
        });

        Assert.Contains("Auth:SharedToken", Flatten(error), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartupFailureNeverEchoesSecretValue()
    {
        const string otherSecret = "another-secret-that-must-not-leak";

        await using var factory = new ApiFactory(
            postgres.ConnectionString,
            sharedToken: "   ",
            extraSettings: new Dictionary<string, string?>
            {
                ["Auth:PreviousToken"] = otherSecret,
            });

        var error = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            var client = factory.CreateClient();
            await client.GetAsync("/health", Ct);
        });

        var text = Flatten(error);
        Assert.DoesNotContain(otherSecret, text, StringComparison.Ordinal);
        Assert.DoesNotContain(postgres.ConnectionString, text, StringComparison.Ordinal);
    }

    private async Task<HttpStatusCode> StatusOf(string path, AuthenticationHeaderValue? header)
    {
        await using var factory = new ApiFactory(postgres.ConnectionString, sharedToken: Token);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = header;

        var response = await client.GetAsync(path, Ct);
        return response.StatusCode;
    }

    private static string Flatten(Exception error)
    {
        var text = new System.Text.StringBuilder();
        for (var current = error; current is not null; current = current.InnerException)
        {
            text.AppendLine(current.ToString());
        }

        return text.ToString();
    }
}
