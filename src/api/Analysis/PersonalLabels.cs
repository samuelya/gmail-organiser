using GmailOrganiser.Fetch;
using GmailOrganiser.Gmail;
using GmailOrganiser.Review;
using GmailOrganiser.Settings;

namespace GmailOrganiser.Analysis;

/// <summary>
/// The person's own filing: Gmail user labels except the app's action and delete labels (names from Settings), which
/// the app sets itself. These are what the labelled scope, the prompt's current labels, grouping and the memory
/// short-circuit look at. <see cref="None"/> (Gmail not reachable) knows no names and no app label ids, so every
/// user label id counts.
/// </summary>
public sealed class PersonalLabels
{
    public static readonly PersonalLabels None = new(new Dictionary<string, string>(StringComparer.Ordinal), []);

    private PersonalLabels(IReadOnlyDictionary<string, string> names, IReadOnlyList<string> appLabelIds)
    {
        Names = names;
        AppLabelIds = appLabelIds;
    }

    /// <summary>Names of the personal labels by id.</summary>
    public IReadOnlyDictionary<string, string> Names { get; }

    /// <summary>Ids of the app's action and delete labels; never personal.</summary>
    public IReadOnlyList<string> AppLabelIds { get; }

    public static PersonalLabels From(IEnumerable<GmailLabel> labels, AppSettings settings)
    {
        var user = labels.Where(l => l.Type == GmailLabelType.User).DistinctBy(l => l.Id, StringComparer.Ordinal).ToList();
        bool IsApp(GmailLabel l) =>
            string.Equals(l.Name.Trim(), settings.ActionLabelName.Trim(), StringComparison.OrdinalIgnoreCase)
            || string.Equals(l.Name.Trim(), settings.DeleteLabelName.Trim(), StringComparison.OrdinalIgnoreCase);
        return new PersonalLabels(
            user.Where(l => !IsApp(l)).ToDictionary(l => l.Id, l => l.Name, StringComparer.Ordinal),
            [.. user.Where(IsApp).Select(l => l.Id)]);
    }

    /// <summary>
    /// From the cached label list; <see cref="None"/> when Gmail is not connected or the list cannot be loaded now
    /// (rate limit, network, token refresh): callers that only count or display degrade instead of failing.
    /// </summary>
    public static async Task<PersonalLabels> LoadAsync(LabelCatalog catalog, AppSettings settings, CancellationToken ct)
    {
        try
        {
            return From(await catalog.GetAsync(ct), settings);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return None;
        }
    }

    public bool IsPersonal(string labelId) => GmailLabelIds.IsUser(labelId) && !AppLabelIds.Contains(labelId, StringComparer.Ordinal);

    /// <summary>The message's personal label ids, distinct and sorted.</summary>
    public IEnumerable<string> IdsOf(MessageRow m) =>
        m.LabelIds.Where(IsPersonal).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);

    /// <summary>The message's personal label names, sorted; an id without a known name is skipped.</summary>
    public IReadOnlyList<string> NamesOf(MessageRow m) =>
        [.. IdsOf(m).Select(id => Names.GetValueOrDefault(id)).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
}
