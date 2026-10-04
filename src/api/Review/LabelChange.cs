using System.Text.Json;
using System.Text.Json.Serialization;

namespace GmailOrganiser.Review;

/// <summary>What a suggestion does to the message's own labels (labelled phase, DESIGN §6.3).</summary>
[JsonConverter(typeof(LabelChangeJsonConverter))]
public enum LabelChange
{
    /// <summary>The message has no user labels.</summary>
    None,

    /// <summary>The topic label is already on the message and nothing is replaced.</summary>
    Keep,

    /// <summary>The topic label is added next to the message's labels.</summary>
    Add,

    /// <summary>One label is replaced by the same name elsewhere in the hierarchy.</summary>
    Move,

    /// <summary>Labels are replaced by another one.</summary>
    Relabel,
}

/// <summary>Serialises <see cref="LabelChange"/> as <c>none | keep | add | move | relabel</c>.</summary>
public sealed class LabelChangeJsonConverter()
    : JsonStringEnumConverter<LabelChange>(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false);

public static class LabelChanges
{
    /// <param name="currentUserLabels">The message's user label names.</param>
    public static LabelChange For(string topicLabel, IReadOnlyList<string> replaceLabels, IReadOnlyList<string> currentUserLabels)
    {
        if (replaceLabels.Count == 1 && string.Equals(Leaf(topicLabel), Leaf(replaceLabels[0]), StringComparison.OrdinalIgnoreCase))
        {
            return LabelChange.Move;
        }

        if (replaceLabels.Count > 0)
        {
            return LabelChange.Relabel;
        }

        if (currentUserLabels.Count == 0)
        {
            return LabelChange.None;
        }

        return currentUserLabels.Any(l => string.Equals(l.Trim(), topicLabel.Trim(), StringComparison.OrdinalIgnoreCase))
            ? LabelChange.Keep
            : LabelChange.Add;
    }

    private static string Leaf(string path) => path.Trim()[(path.Trim().LastIndexOf('/') + 1)..].Trim();
}
