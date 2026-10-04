using GmailOrganiser.Data;
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

    /// <summary>Why <paramref name="address"/> is allowlisted, or null when it is not.</summary>
    public string? Reason(string address) =>
        Addresses.Contains(address) ? SenderReason
        : CoversDomain(DomainOf(address)) ? DomainReason
        : null;

    /// <summary>Whether <paramref name="domain"/> equals a listed domain or is a subdomain of one.</summary>
    public bool CoversDomain(string domain) =>
        domain.Length > 0 && Domains.Any(d => domain == d || domain.EndsWith("." + d, StringComparison.Ordinal));

    /// <summary>The part after the last <c>@</c>, lower-cased; empty when there is none.</summary>
    public static string DomainOf(string address) =>
        address.LastIndexOf('@') is var at and >= 0 ? address[(at + 1)..].ToLowerInvariant() : "";
}

public static class AllowlistLoader
{
    /// <summary>The allowlisted addresses (<c>senders.allowlisted</c>) and the domains in <paramref name="settings"/>.</summary>
    public static async Task<Allowlist> LoadAsync(AppDbContext db, AppSettings settings, CancellationToken ct) =>
        new((await db.Senders.AsNoTracking().Where(s => s.Allowlisted).Select(s => s.Address).ToListAsync(ct))
                .ToHashSet(StringComparer.Ordinal),
            settings.Protection.AllowlistedDomains);
}
