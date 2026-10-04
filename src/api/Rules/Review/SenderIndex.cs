using GmailOrganiser.Analysis;

namespace GmailOrganiser.Rules.Review;

/// <summary>
/// Message counts by sender address and by every domain suffix, so a <c>from</c> filter's count is one lookup per term
/// rather than a scan of every sender. Matches as <see cref="FilterCriteriaMapping.FromTermMatches"/> does.
/// </summary>
public sealed class SenderIndex
{
    private readonly Dictionary<string, int> addresses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> domains = new(StringComparer.Ordinal);

    private SenderIndex()
    {
    }

    public static SenderIndex Build(IEnumerable<(string Address, int Count)> senders)
    {
        ArgumentNullException.ThrowIfNull(senders);
        var index = new SenderIndex();
        foreach (var (raw, count) in senders)
        {
            var address = raw.ToLowerInvariant();
            index.addresses[address] = index.addresses.GetValueOrDefault(address) + count;
            var at = address.LastIndexOf('@');
            if (at < 0 || at == address.Length - 1)
            {
                continue;
            }

            // "a@mail.example.com" counts towards mail.example.com, example.com and com.
            for (var domain = address[(at + 1)..]; ; domain = domain[(domain.IndexOf('.', StringComparison.Ordinal) + 1)..])
            {
                index.domains[domain] = index.domains.GetValueOrDefault(domain) + count;
                if (!domain.Contains('.', StringComparison.Ordinal))
                {
                    break;
                }
            }
        }

        return index;
    }

    /// <summary>Messages from any of the lower-cased <paramref name="terms"/>, each message counted once.</summary>
    public int Count(IReadOnlyList<string> terms)
    {
        ArgumentNullException.ThrowIfNull(terms);
        var listed = terms.Where(t => t[0] == '@').Select(t => t[1..]).Distinct(StringComparer.Ordinal).ToList();

        // A term another domain term already covers would count its messages twice.
        var domainTerms = listed.Where(d => !Allowlist.CoversDomain([.. listed.Where(o => o != d)], d)).ToList();
        var addressTerms = terms.Where(t => t[0] != '@').Distinct(StringComparer.Ordinal)
            .Where(a => !Allowlist.CoversDomain(domainTerms, a[(a.LastIndexOf('@') + 1)..]));
        return domainTerms.Sum(d => domains.GetValueOrDefault(d)) + addressTerms.Sum(a => addresses.GetValueOrDefault(a));
    }
}
