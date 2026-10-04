using System.Text.Json;
using System.Text.Json.Serialization;
using GmailOrganiser.Gmail;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Rules;

/// <summary>
/// One Gmail filter in the local snapshot (<c>filters</c>), keyed by Gmail's filter id. A filter Gmail no longer lists
/// keeps its row with <see cref="DeletedAt"/> set, so a review can show what was removed and restore it.
/// </summary>
public sealed class FilterRow
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public string Id { get; set; } = "";

    /// <summary><see cref="GmailFilterCriteria"/> as JSON; read it with <see cref="ReadCriteria"/>.</summary>
    public string Criteria { get; set; } = "{}";

    /// <summary><see cref="GmailFilterAction"/> as JSON; read it with <see cref="ReadAction"/>.</summary>
    public string Action { get; set; } = "{}";

    /// <summary><see cref="FilterSnapshot.Summarise"/> of the criteria, for sorting and search.</summary>
    public string CriteriaSummary { get; set; } = "";
    public bool CreatedByApp { get; set; }

    /// <summary>The id of the deleted filter this one re-creates.</summary>
    public string? RestoredFrom { get; set; }
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public bool DeletedByApp { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public static string WriteCriteria(GmailFilterCriteria criteria) => JsonSerializer.Serialize(criteria, Json);

    public static string WriteAction(GmailFilterAction action) => JsonSerializer.Serialize(action, Json);

    public GmailFilterCriteria ReadCriteria() => JsonSerializer.Deserialize<GmailFilterCriteria>(Criteria, Json) ?? new();

    public GmailFilterAction ReadAction()
    {
        var action = JsonSerializer.Deserialize<GmailFilterAction>(Action, Json);
        return new GmailFilterAction(action?.AddLabelIds ?? [], action?.RemoveLabelIds ?? [], action?.Forward);
    }

    internal static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<FilterRow>(e =>
        {
            e.ToTable("filters");
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).ValueGeneratedNever();
            e.Property(r => r.Criteria).HasColumnType("jsonb").IsRequired();
            e.Property(r => r.Action).HasColumnType("jsonb").IsRequired();
            e.Property(r => r.CriteriaSummary).IsRequired();
            e.HasIndex(r => r.DeletedAt);
        });
    }
}
