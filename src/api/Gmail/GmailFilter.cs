namespace GmailOrganiser.Gmail;

/// <summary>How a filter compares a message's size with <see cref="GmailFilterCriteria.Size"/>.</summary>
public enum GmailSizeComparison
{
    Smaller,
    Larger,
}

/// <summary>The criteria of a Gmail filter (<c>users.settings.filters</c>); every part is optional and they all apply.</summary>
/// <param name="Query">Gmail search syntax the message must match.</param>
/// <param name="NegatedQuery">Gmail search syntax the message must not match.</param>
/// <param name="Size">A size in bytes, compared by <paramref name="SizeComparison"/>.</param>
public sealed record GmailFilterCriteria(
    string? From = null,
    string? To = null,
    string? Subject = null,
    string? Query = null,
    string? NegatedQuery = null,
    bool? HasAttachment = null,
    bool? ExcludeChats = null,
    int? Size = null,
    GmailSizeComparison? SizeComparison = null)
{
    public bool IsEmpty =>
        string.IsNullOrEmpty(From) && string.IsNullOrEmpty(To) && string.IsNullOrEmpty(Subject)
        && string.IsNullOrEmpty(Query) && string.IsNullOrEmpty(NegatedQuery)
        && HasAttachment is null or false && ExcludeChats is null or false && Size is null;
}

/// <summary>What a Gmail filter does to a matching message.</summary>
/// <param name="Forward">An address the message is forwarded to; read only, the app never creates a forwarding filter.</param>
public sealed record GmailFilterAction(IReadOnlyList<string> AddLabelIds, IReadOnlyList<string> RemoveLabelIds, string? Forward = null)
{
    public bool IsEmpty => AddLabelIds.Count == 0 && RemoveLabelIds.Count == 0 && string.IsNullOrEmpty(Forward);
}

public sealed record GmailFilter(string Id, GmailFilterCriteria Criteria, GmailFilterAction Action)
{
    /// <summary>Validates a <see cref="IGmailClient.CreateFilterAsync"/> request.</summary>
    public static void EnsureValidCreate(GmailFilterCriteria criteria, GmailFilterAction action)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(action.AddLabelIds);
        ArgumentNullException.ThrowIfNull(action.RemoveLabelIds);
        if (criteria.IsEmpty)
        {
            throw new ArgumentException("A filter needs at least one criterion.", nameof(criteria));
        }

        if (action.Forward is not null)
        {
            throw new ArgumentException("The app never creates a forwarding filter.", nameof(action));
        }

        if (action.IsEmpty)
        {
            throw new ArgumentException("A filter needs at least one label to add or remove.", nameof(action));
        }
    }
}
