using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Api.Infrastructure.Auth;

/// <summary>
/// Authenticates `Authorization: Bearer &lt;token&gt;` against the configured shared
/// secret. The comparison is fixed-time and length-checked, so a token that is a
/// prefix or an extension of the real one is rejected like any other wrong value.
/// </summary>
public sealed class SharedSecretAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptions<SharedSecretOptions> secret)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "SharedSecret";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var values))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var header = values.ToString();
        const string prefix = "Bearer ";

        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(AuthenticateResult.Fail("The Authorization header is not a Bearer token."));
        }

        var presented = header[prefix.Length..].Trim();
        var configured = secret.Value.SharedToken ?? string.Empty;

        if (!FixedTimeEquals(presented, configured))
        {
            // No token value in the message, at either end: this text reaches logs.
            return Task.FromResult(AuthenticateResult.Fail("The presented token does not match."));
        }

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "local")],
            SchemeName);

        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }

    private static bool FixedTimeEquals(string presented, string configured)
    {
        var left = Encoding.UTF8.GetBytes(presented);
        var right = Encoding.UTF8.GetBytes(configured);

        // FixedTimeEquals requires equal lengths; different lengths are simply not
        // equal, which is also what rejects a prefix and an extension.
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }
}
