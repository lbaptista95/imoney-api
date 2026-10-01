namespace Api.Features.Accounts;

/// <summary>What kind of account this is. Persisted as text, not as an ordinal.</summary>
public enum AccountKind
{
    CHECKING,
    CREDIT_CARD,
}
