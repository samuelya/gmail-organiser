using System.Text.Json;
using System.Text.Json.Serialization;

namespace GmailOrganiser.Gmail;

/// <summary>How a filter compares a message's size with <see cref="GmailFilterCriteria.Size"/>.</summary>
[JsonConverter(typeof(GmailSizeComparisonJsonConverter))]
public enum GmailSizeComparison
{
    Smaller,
    Larger,
}

/// <summary>The one mapping between <see cref="GmailSizeComparison"/> and Gmail's <c>sizeComparison</c> strings.</summary>
public static class GmailSizeComparisons
{
    public static string ToGmailString(this GmailSizeComparison comparison) => comparison switch
    {
        GmailSizeComparison.Smaller => "smaller",
        GmailSizeComparison.Larger => "larger",
        _ => throw new ArgumentOutOfRangeException(nameof(comparison), comparison, null),
    };

    /// <summary>The comparison Gmail's <paramref name="value"/> names; null for absent, <c>unspecified</c> or unknown.</summary>
    public static GmailSizeComparison? Parse(string? value) => value?.ToLowerInvariant() switch
    {
        "smaller" => GmailSizeComparison.Smaller,
        "larger" => GmailSizeComparison.Larger,
        _ => null,
    };
}

/// <summary>Stores <see cref="GmailSizeComparison"/> as Gmail's string.</summary>
public sealed class GmailSizeComparisonJsonConverter : JsonConverter<GmailSizeComparison>
{
    public override GmailSizeComparison Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        GmailSizeComparisons.Parse(reader.GetString()) ?? throw new JsonException("Unknown size comparison.");

    public override void Write(Utf8JsonWriter writer, GmailSizeComparison value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToGmailString());
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
    /// <summary>True when a criterion other than <see cref="ExcludeChats"/> matches mail; Gmail rejects a filter without one.</summary>
    public bool MatchesMail =>
        !string.IsNullOrWhiteSpace(From) || !string.IsNullOrWhiteSpace(To) || !string.IsNullOrWhiteSpace(Subject)
        || !string.IsNullOrWhiteSpace(Query) || !string.IsNullOrWhiteSpace(NegatedQuery) || HasAttachment == true || Size is not null;
}

/// <summary>What a Gmail filter does to a matching message.</summary>
/// <param name="Forward">An address the message is forwarded to; read only, the app never creates a forwarding filter.</param>
public sealed record GmailFilterAction(IReadOnlyList<string> AddLabelIds, IReadOnlyList<string> RemoveLabelIds, string? Forward = null)
{
    public bool IsEmpty => AddLabelIds.Count == 0 && RemoveLabelIds.Count == 0 && string.IsNullOrWhiteSpace(Forward);
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
        if (!criteria.MatchesMail)
        {
            throw new ArgumentException("A filter needs at least one criterion besides excluding chats.", nameof(criteria));
        }

        if (criteria.Size is null != criteria.SizeComparison is null || criteria.Size < 0)
        {
            throw new ArgumentException("A size criterion needs a non-negative size and a comparison.", nameof(criteria));
        }

        if (action.Forward is not null)
        {
            throw new ArgumentException("The app never creates a forwarding filter.", nameof(action));
        }

        if (action.IsEmpty)
        {
            throw new ArgumentException("A filter needs at least one label to add or remove.", nameof(action));
        }

        if (action.AddLabelIds.Concat(action.RemoveLabelIds).Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("A filter label id must not be blank.", nameof(action));
        }
    }
}
