using System.Buffers.Text;
using System.Globalization;
using System.Text;

namespace Api.Features.Transactions;

/// <summary>
/// The opaque paging cursor (door 2): base64 over the (occurred_at, id) pair that the
/// keyset query continues from. Opaque on purpose - the client must not build one,
/// so the encoding stays ours to change.
/// </summary>
public sealed record TransactionCursor(DateTimeOffset OccurredAt, Guid Id)
{
    private const char Separator = '|';

    public string Encode()
    {
        var payload = string.Create(
            CultureInfo.InvariantCulture,
            $"{OccurredAt.UtcDateTime:O}{Separator}{Id:D}");
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));
    }

    /// <summary>
    /// Decodes a cursor, returning false for anything this API did not issue: text
    /// that is not base64, base64 whose payload has the wrong shape, and a truncated
    /// cursor. All three are a 400 naming the field, never a 500.
    /// </summary>
    public static bool TryDecode(string cursor, out TransactionCursor? decoded)
    {
        decoded = null;

        var buffer = new byte[Base64.GetMaxDecodedFromUtf8Length(cursor.Length)];
        if (!Convert.TryFromBase64String(cursor, buffer, out var written))
        {
            return false;
        }

        var payload = Encoding.UTF8.GetString(buffer, 0, written);
        var parts = payload.Split(Separator);
        if (parts.Length != 2)
        {
            return false;
        }

        if (!DateTimeOffset.TryParse(
                parts[0],
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var occurredAt))
        {
            return false;
        }

        if (!Guid.TryParse(parts[1], out var id))
        {
            return false;
        }

        decoded = new TransactionCursor(occurredAt, id);
        return true;
    }
}
