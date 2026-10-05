using GmailOrganiser.Fetch;
using GmailOrganiser.Settings;

namespace GmailOrganiser.Analysis.Grouping;

/// <summary>
/// Packs one-off senders into shared snippet-only prompts (#376): individual groups of unprotected mail, in the order
/// given, fill the first open pack without their canonical sender, up to the pack size. A pack takes the place of its
/// first member; every member is a representative. A pack of one stays an individual group, and so does protected
/// mail (its body and attachments decide), and any group <c>keep</c> names (the run's memory-covered singles).
/// Pure: the same input gives the same packs.
/// </summary>
public static class SingletonPacker
{
    public const string KeyPrefix = "pack:";

    public static IReadOnlyList<MessageGroup> Pack(
        IReadOnlyList<MessageGroup> groups, Guid runId, int packSize, Allowlist allowlist, ProtectionSettings protection,
        Func<MessageGroup, bool>? keep = null)
    {
        packSize = Math.Min(packSize, SettingsValidation.MaxAnalysisPackSize);
        if (packSize < 2)
        {
            return groups;
        }

        // Slots keep the input order: a group, or the index of the pack that sits at its first member's place.
        var slots = new List<(MessageGroup? Group, int Pack)>();
        var packs = new List<List<MessageRow>>();
        var open = new List<(int Index, HashSet<string> Senders)>();
        foreach (var group in groups)
        {
            if (!group.Individual || group.Packed || group.Members.Count != 1 || keep?.Invoke(group) == true
                || MessageProtection.IsProtected(group.Members[0], allowlist, protection))
            {
                slots.Add((group, -1));
                continue;
            }

            var message = group.Members[0];
            var sender = SenderOf(message);
            var target = open.FindIndex(p => !p.Senders.Contains(sender));
            if (target < 0)
            {
                packs.Add([]);
                open.Add((packs.Count - 1, new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
                slots.Add((null, packs.Count - 1));
                target = open.Count - 1;
            }

            var (index, senders) = open[target];
            packs[index].Add(message);
            senders.Add(sender);
            if (packs[index].Count == packSize)
            {
                open.RemoveAt(target);
            }
        }

        var result = new List<MessageGroup>(slots.Count);
        var number = 0;
        foreach (var (group, pack) in slots)
        {
            if (group is not null)
            {
                result.Add(group);
            }
            else if (packs[pack] is [var single])
            {
                result.Add(AnalysisGrouper.Single(single));
            }
            else
            {
                var members = packs[pack];
                result.Add(new MessageGroup(
                    $"{KeyPrefix}{runId}:{++number}", "", $"{members.Count} one-off senders", members, [.. members.Select(m => m.Id)],
                    Individual: false, Packed: true));
            }
        }

        return result;
    }

    private static string SenderOf(MessageRow message) =>
        string.IsNullOrEmpty(message.CanonicalAddress) ? message.FromAddress : message.CanonicalAddress;
}
