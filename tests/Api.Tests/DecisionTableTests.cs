using System.Text;
using System.Text.Encodings.Web;
using Api.Features.Transactions;
using Api.Infrastructure.Auth;
using Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Api.Tests;

/// <summary>
/// The decision tables at their own level (C62-C65). The HTTP tests prove the
/// contract at the boundary; these prove each row of each table where it is decided,
/// with nothing in between - which is what the first row of the Test policy asks for,
/// and what the first verification found missing (F6).
/// </summary>
public sealed class DecisionTableTests
{
    // ---- C62: TransactionCursor ----------------------------------------------

    [Fact]
    public void CursorRoundTripsAndRejectsEachInvalidForm()
    {
        var original = new TransactionCursor(
            new DateTimeOffset(2026, 9, 30, 12, 34, 56, 789, TimeSpan.Zero),
            Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e"));

        var encoded = original.Encode();
        Assert.True(TransactionCursor.TryDecode(encoded, out var decoded));
        Assert.Equal(original.OccurredAt, decoded!.OccurredAt);
        Assert.Equal(original.Id, decoded.Id);

        // "Wrong payload" is more than one shape. A single-segment string is rejected
        // by the date parse whatever the segment check does, so on its own it cannot
        // tell a correct check from a loose one; an extra segment and a missing id can.
        string Payload(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
        string[] invalid =
        [
            "not base64 at all!!",
            Payload("hello world"),
            Payload("2026-09-30T12:34:56.7890000Z|0f8fad5b-d9cb-469f-a165-70867728950e|extra"),
            Payload("2026-09-30T12:34:56.7890000Z"),
            encoded[..(encoded.Length / 2)],
        ];

        foreach (var cursor in invalid)
        {
            Assert.False(TransactionCursor.TryDecode(cursor, out var rejected), $"accepted '{cursor}'");
            Assert.Null(rejected);
        }
    }

    // ---- C63: limit resolution -----------------------------------------------

    [Theory]
    [InlineData(null, true, 50)]
    [InlineData(1, true, 1)]
    [InlineData(200, true, 200)]
    [InlineData(201, true, 200)]
    [InlineData(0, false, 0)]
    [InlineData(-1, false, 0)]
    public void LimitResolutionCoversEveryEdge(int? requested, bool valid, int pageSize)
    {
        Assert.Equal(valid, PageLimit.TryResolve(requested, out var resolved));
        if (valid)
        {
            Assert.Equal(pageSize, resolved);
        }
    }

    // ---- C64: SharedSecretAuthenticationHandler ------------------------------

    private const string Token = "the-configured-token";

    [Theory]
    [InlineData(null, "none")]
    [InlineData("Basic dXNlcjpwYXNz", "fail")]
    [InlineData("Bearer ", "fail")]
    [InlineData("Bearer the-configured-toke", "fail")]
    [InlineData("Bearer the-configured-tokenx", "fail")]
    [InlineData("Bearer a-different-token", "fail")]
    [InlineData("Bearer the-configured-token", "success")]
    public async Task HandlerDecidesEveryHeaderShape(string? header, string expected)
    {
        var result = await AuthenticateAsync(header);

        var actual = result.None ? "none" : result.Succeeded ? "success" : "fail";
        Assert.Equal(expected, actual);
    }

    private static async Task<AuthenticateResult> AuthenticateAsync(string? header)
    {
        var handler = new SharedSecretAuthenticationHandler(
            new StaticOptionsMonitor(new AuthenticationSchemeOptions()),
            NullLoggerFactory.Instance,
            UrlEncoder.Default,
            Options.Create(new SharedSecretOptions { SharedToken = Token }));

        var context = new DefaultHttpContext();
        if (header is not null)
        {
            context.Request.Headers.Authorization = header;
        }

        await handler.InitializeAsync(
            new AuthenticationScheme(
                SharedSecretAuthenticationHandler.SchemeName,
                displayName: null,
                typeof(SharedSecretAuthenticationHandler)),
            context);

        return await handler.AuthenticateAsync();
    }

    private sealed class StaticOptionsMonitor(AuthenticationSchemeOptions value)
        : IOptionsMonitor<AuthenticationSchemeOptions>
    {
        public AuthenticationSchemeOptions CurrentValue => value;

        public AuthenticationSchemeOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<AuthenticationSchemeOptions, string?> listener) => null;
    }

    // ---- C65: ConnectionSecretRedactor ---------------------------------------

    [Fact]
    public void RedactorMasksEverySecretForm()
    {
        const string password = "pw-to-mask";
        const string connection = $"Host=db;Database=imoney;Username=imoney;Password={password}";

        // The password alone, wherever it appears.
        Assert.DoesNotContain(
            password,
            ConnectionSecretRedactor.Redact($"auth failed for password {password}", connection),
            StringComparison.Ordinal);

        // The whole connection string, quoted back by an error message.
        Assert.DoesNotContain(
            connection,
            ConnectionSecretRedactor.Redact($"could not connect using '{connection}'", connection),
            StringComparison.Ordinal);

        // A connection string that does not parse is still masked as a whole.
        const string unparseable = "this=is;not a=valid;;;connection;string=at all";
        Assert.DoesNotContain(
            unparseable,
            ConnectionSecretRedactor.Redact($"echo {unparseable}", unparseable),
            StringComparison.Ordinal);

        // Empty text is returned as it came.
        Assert.Equal(string.Empty, ConnectionSecretRedactor.Redact(string.Empty, connection));
    }
}
