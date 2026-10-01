using Microsoft.Extensions.Options;

namespace Api.Infrastructure.Auth;

/// <summary>
/// The single shared secret that guards /v1. This is the minimum lock until real
/// authentication arrives (ADR 0011); it is not user management and there is no
/// rotation, which is enough for one person running this locally.
/// </summary>
public sealed class SharedSecretOptions
{
    public const string SectionName = "Auth";

    /// <summary>Configured as `Auth:SharedToken`, in dotnet user-secrets or the environment.</summary>
    public string? SharedToken { get; set; }
}

/// <summary>
/// Refuses to start without the token, or with one that is only whitespace, which
/// would otherwise match an empty Bearer value. Registered with ValidateOnStart, so
/// it fires on every host start - including the OpenAPI generator's, which starts
/// this application at build time. That is why Api.csproj hands the generator its
/// own value instead of this validator keeping an exemption for it.
/// </summary>
public sealed class SharedSecretOptionsValidator : IValidateOptions<SharedSecretOptions>
{
    public ValidateOptionsResult Validate(string? name, SharedSecretOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.SharedToken))
        {
            // Names the key and nothing else. Echoing the configured value, or any
            // neighbouring value, would put a secret in a stack trace.
            return ValidateOptionsResult.Fail(
                "Missing configuration 'Auth:SharedToken'. Set it with "
                + "`dotnet user-secrets set Auth:SharedToken <value>` or the "
                + "Auth__SharedToken environment variable. The API refuses to start "
                + "without it, so that /v1 is never served unguarded.");
        }

        return ValidateOptionsResult.Success;
    }
}
