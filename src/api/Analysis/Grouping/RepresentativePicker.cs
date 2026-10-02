using GmailOrganiser.Fetch;

namespace GmailOrganiser.Analysis.Grouping;

/// <summary>
/// Picks the members of a group that go to the LLM: every protected member, then newest, oldest, the longest distinct
/// subject and members spread over the group's date range, until there are <c>k</c> (or more, when protected members
/// alone exceed <c>k</c>).
/// </summary>
public static class RepresentativePicker
{
    public static IReadOnlyList<string> Pick(MessageGroup group, int k, IReadOnlySet<string> allowlistedSenders)
    {
        // Oldest first, so an index is a position on the group's timeline.
        var byDate = group.Members.OrderBy(m => m.InternalDate).ThenBy(m => m.Id, StringComparer.Ordinal).ToList();
        var target = Math.Min(Math.Max(k, 1), byDate.Count);
        var picked = new List<int>();

        for (var i = 0; i < byDate.Count; i++)
        {
            if (MessageProtection.IsProtected(byDate[i], allowlistedSenders))
            {
                picked.Add(i);
            }
        }

        Add(byDate.Count - 1);
        Add(0);
        if (picked.Count < target)
        {
            var subjects = picked.Select(i => byDate[i].Subject ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase);
            var longest = Enumerable.Range(0, byDate.Count)
                .Where(i => !picked.Contains(i) && !subjects.Contains(byDate[i].Subject ?? ""))
                .OrderByDescending(i => byDate[i].Subject?.Length ?? 0)
                .ThenByDescending(i => i)
                .Select(i => (int?)i)
                .FirstOrDefault();
            if (longest is { } l)
            {
                Add(l);
            }
        }

        while (picked.Count < target)
        {
            Add(FarthestFromPicked(byDate.Count, picked));
        }

        return picked.Select(i => byDate[i].Id).ToList();

        void Add(int index)
        {
            if (picked.Count < target && !picked.Contains(index))
            {
                picked.Add(index);
            }
        }
    }

    /// <summary>The unpicked position with the largest gap to its nearest picked one; ties go to the older member.</summary>
    private static int FarthestFromPicked(int count, List<int> picked)
    {
        var best = -1;
        var bestGap = -1;
        for (var i = 0; i < count; i++)
        {
            if (picked.Contains(i))
            {
                continue;
            }

            var gap = picked.Count == 0 ? int.MaxValue : picked.Min(p => Math.Abs(p - i));
            if (gap > bestGap)
            {
                best = i;
                bestGap = gap;
            }
        }

        return best;
    }
}
