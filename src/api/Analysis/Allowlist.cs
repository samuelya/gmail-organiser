using GmailOrganiser.Data;
using GmailOrganiser.Gmail;
using GmailOrganiser.Senders;
using GmailOrganiser.Settings;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Analysis;

/// <summary>
/// Senders that are always protected (#178, domains #203): an exact allowlisted address, or an address whose domain is
/// a listed domain or one of its subdomains. Load it once per job or request with <see cref="AllowlistLoader"/>.
/// </summary>
public sealed record Allowlist(IReadOnlySet<string> Addresses, IReadOnlyList<string> Domains)
{
    public const string SenderReason = "allowlisted sender";
    public const string DomainReason = "allowlisted domain";

    public static readonly Allowlist Empty = new(new HashSet<string>(StringComparer.Ordinal), []);

    /// <summary>Why <paramref name="address"/> (lower-case, as stored) is allowlisted, or null when it is not.</summary>
    public string? Reason(string address) =>
        Addresses.Contains(address) ? SenderReason
        : CoversDomain(Domains, new SenderAddress(address, null).Domain) ? DomainReason
        : null;

    /// <summary>Whether <paramref name="domain"/> equals one of <paramref name="domains"/> or is a subdomain of one.</summary>
    public static bool CoversDomain(IReadOnlyList<string> domains, string domain) =>
        domain.Length > 0 && domains.Any(d => domain == d || domain.EndsWith("." + d, StringComparison.Ordinal));
}

public static class AllowlistLoader
{
    /// <summary>The allowlisted addresses (<c>senders.allowlisted</c>) and the domains in <paramref name="settings"/>.</summary>
    public static Task<Allowlist> LoadAsync(AppDbContext db, AppSettings settings, CancellationToken ct) =>
        LoadAsync(db, settings, db.Senders.AsNoTracking().Where(s => s.Allowlisted), ct);

    /// <summary>As above, reading only the allowlisted addresses among <paramref name="addresses"/>.</summary>
    public static Task<Allowlist> LoadAsync(
        AppDbContext db, AppSettings settings, IReadOnlyCollection<string> addresses, CancellationToken ct) =>
        LoadAsync(db, settings, db.Senders.AsNoTracking().Where(s => s.Allowlisted && addresses.Contains(s.Address)), ct);

    private static async Task<Allowlist> LoadAsync(
        AppDbContext db, AppSettings settings, IQueryable<SenderRow> allowlisted, CancellationToken ct) =>
        new((await allowlisted.Select(s => s.Address).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal),
            settings.Protection.AllowlistedDomains);
}
