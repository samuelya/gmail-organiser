using System.Buffers;
using GmailOrganiser.Data;
using GmailOrganiser.Gmail;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Senders;

/// <summary>
/// Sets <c>senders.allowlisted</c> for one address. Allowlisting an address never fetched creates a stub row (counts 0,
/// never seen) that a later fetch fills in. Touches neither Gmail nor suggestions: protection reads the flag when it
/// plans an apply.
/// </summary>
public sealed class SenderAllowlist(AppDbContext db, TimeProvider time)
{
    /// <summary>RFC 5321 caps a forward path at 256 octets; 320 (64 + @ + 255) is the common upper bound.</summary>
    public const int MaxAddressLength = 320;

    /// <summary>RFC 5322 specials: a fetched address never contains them once its angle brackets are stripped.</summary>
    private static readonly SearchValues<char> Specials = SearchValues.Create("<>()[]\\\",;:");

    /// <summary>
    /// The address as fetch stores it (<see cref="GmailMetadataMapper.ParseFrom"/>: one surrounding <c>&lt;…&gt;</c>
    /// stripped, trimmed, lower-case), or null with <paramref name="error"/> when it is not a single plain
    /// <c>local@domain</c>: no whitespace, quoted local part, list separator or other RFC 5322 special.
    /// </summary>
    public static string? Normalise(string? address, out string? error)
    {
        var value = address?.Trim().ToLowerInvariant() ?? "";
        if (value.Length >= 2 && value[0] == '<' && value[^1] == '>')
        {
            value = value[1..^1].Trim();
        }

        var at = value.IndexOf('@');
        error = value.Length is 0 or > MaxAddressLength
                || at <= 0 || at == value.Length - 1 || value.IndexOf('@', at + 1) >= 0
                || value.AsSpan().ContainsAny(Specials)
                || value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))
            ? $"Must be one plain address (local@domain) of at most {MaxAddressLength} characters, without whitespace, quotes or any of <>()[]\\,;:."
            : null;
        return error is null ? value : null;
    }

    /// <summary>
    /// Sets the flag on a normalised <paramref name="address"/> and returns the row; null when the address is unknown
    /// and <paramref name="allowlisted"/> is false.
    /// </summary>
    public async Task<SenderRow?> SetAsync(string address, bool allowlisted, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (allowlisted)
        {
            // One atomic upsert: a concurrent request or fetch chunk inserting the same address cannot make it fail.
            var domain = new SenderAddress(address, null).Domain;
            await db.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO senders (address, domain, display_name, total_count, analysed_count, applied_count, last_seen_at, allowlisted, updated_at)
                VALUES ({address}, {domain}, NULL, 0, 0, 0, NULL, TRUE, {now})
                ON CONFLICT (address) DO UPDATE SET allowlisted = TRUE, updated_at = EXCLUDED.updated_at
                """,
                ct);
        }
        else if (await db.Senders.Where(s => s.Address == address)
            .ExecuteUpdateAsync(set => set.SetProperty(s => s.Allowlisted, false).SetProperty(s => s.UpdatedAt, now), ct) == 0)
        {
            return null;
        }

        return await db.Senders.AsNoTracking().SingleAsync(s => s.Address == address, ct);
    }
}
