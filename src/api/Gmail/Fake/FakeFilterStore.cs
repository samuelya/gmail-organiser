using System.Globalization;
using System.Net;

namespace GmailOrganiser.Gmail.Fake;

/// <summary>
/// The fake account's Gmail filters: three synthetic filters, one of them adding a label id the mailbox does not have
/// (a filter left behind by a deleted label). New filters get <c>fake-filter-&lt;n&gt;</c> ids. Not thread-safe;
/// <see cref="FakeGmailClient"/> calls it under its lock.
/// </summary>
public sealed class FakeFilterStore
{
    /// <summary>The label id the third seeded filter adds; no label of the fake mailbox has it.</summary>
    public const string MissingLabelId = "Label_missing";

    public static readonly IReadOnlyList<GmailFilter> Seed =
    [
        new("fake-filter-1", new GmailFilterCriteria(From: "news@example.com"), new GmailFilterAction(["Label_1"], ["INBOX"])),
        new(
            "fake-filter-2",
            new GmailFilterCriteria(Subject: "Synthetic receipt", HasAttachment: true),
            new GmailFilterAction(["Label_3"], ["UNREAD"])),
        new("fake-filter-3", new GmailFilterCriteria(To: "lists@example.com"), new GmailFilterAction([MissingLabelId], [])),
    ];

    private readonly List<GmailFilter> filters = [.. Seed];
    private int nextId = Seed.Count;

    public IReadOnlyList<GmailFilter> All => [.. filters];

    /// <summary>Adds a filter; one with the same criteria and action is refused with Gmail's 400 "Filter already exists".</summary>
    public GmailFilter Create(GmailFilterCriteria criteria, GmailFilterAction action)
    {
        if (filters.Any(f => f.Criteria == criteria
            && f.Action.AddLabelIds.SequenceEqual(action.AddLabelIds)
            && f.Action.RemoveLabelIds.SequenceEqual(action.RemoveLabelIds)))
        {
            var refused = GmailRetryPolicy.CreateApiException(HttpStatusCode.BadRequest, "failedPrecondition");
            refused.Error.Message = "Filter already exists";
            throw refused;
        }

        var filter = new GmailFilter(string.Create(CultureInfo.InvariantCulture, $"fake-filter-{++nextId}"), criteria, action);
        filters.Add(filter);
        return filter;
    }

    /// <summary>Removes filter <paramref name="id"/>; an unknown id is a no-op, as Gmail's 404 is for the client.</summary>
    public void Delete(string id) => filters.RemoveAll(f => string.Equals(f.Id, id, StringComparison.Ordinal));

    /// <summary>Puts <paramref name="filter"/> back with its id, as if it had never been deleted.</summary>
    public void Restore(GmailFilter filter)
    {
        Delete(filter.Id);
        filters.Add(filter);
    }
}
