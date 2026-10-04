using GmailOrganiser.Analysis.Grouping;
using GmailOrganiser.Claude;

namespace GmailOrganiser.Review;

/// <summary>Validation of the <see cref="GroupRef"/> lists the Claude review and alternative endpoints take.</summary>
public static class GroupRefs
{
    /// <summary>
    /// The groups with the sender address trimmed and lower-cased; adds a <c>groups</c> error when there are more than
    /// <paramref name="max"/>, a sender address or group key is missing or too long, or a status is unknown.
    /// </summary>
    public static GroupRef[] Normalise(GroupRef?[]? groups, int max, Dictionary<string, string[]> errors)
    {
        if (groups is { } all && all.Length > max)
        {
            errors["groups"] = [$"At most {max} groups."];
            return [];
        }

        var normalised = new List<GroupRef>();
        foreach (var g in groups ?? [])
        {
            var sender = g?.SenderAddress?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(sender) || sender.Length > AnalysisPreviewEndpoint.MaxSenderAddressLength
                || string.IsNullOrEmpty(g!.GroupKey) || g.GroupKey.Length > ReviewEndpoints.MaxGroupKeyLength
                || ReviewQuery.ParseStatus(g.Status) is null)
            {
                errors["groups"] = [$"Each group needs a sender address (at most {AnalysisPreviewEndpoint.MaxSenderAddressLength} characters), a group key (at most {ReviewEndpoints.MaxGroupKeyLength}) and, when given, a status of pending, approved or rejected."];
                return [];
            }

            normalised.Add(g with { SenderAddress = sender });
        }

        return [.. normalised];
    }
}
