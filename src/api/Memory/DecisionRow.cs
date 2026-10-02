using GmailOrganiser.Analysis;
using GmailOrganiser.Data;
using Microsoft.EntityFrameworkCore;
using Pgvector;

namespace GmailOrganiser.Memory;

public enum DecisionOutcome
{
    Approved,
    Rejected,
}

/// <summary>
/// An approved or rejected suggestion kept as LLM memory (<c>decisions</c>). <see cref="MessageId"/> has no foreign
/// key so memory survives a purge of the message.
/// </summary>
public sealed class DecisionRow
{
    public Guid Id { get; set; }
    public string? MessageId { get; set; }
    public string SenderAddress { get; set; } = "";

    /// <summary>Normalised like <see cref="Analysis.Grouping.GroupKey"/> does.</summary>
    public string? ListId { get; set; }

    /// <summary>
    /// The message's <see cref="Analysis.Grouping.GroupKey"/> (list or sender and category, plus subject template): the
    /// scope a memory pattern covers.
    /// </summary>
    public string? ScopeKey { get; set; }
    public string? SubjectTemplate { get; set; }
    public string TopicLabel { get; set; } = "";
    public bool NeedsAction { get; set; }
    public bool ToBeDeleted { get; set; }
    public DecisionOutcome Outcome { get; set; }
    public SuggestionSource Source { get; set; }
    public bool Edited { get; set; }

    /// <summary>
    /// Untyped <c>vector</c> (no fixed dimension): the embedding model is user-chosen, so similarity queries filter by
    /// <see cref="EmbeddingModel"/> first.
    /// </summary>
    public Vector? Embedding { get; set; }
    public string? EmbeddingModel { get; set; }

    /// <summary>
    /// When the embedder last rejected this decision on its own while other decisions embedded; the background pass
    /// skips it until <see cref="DecisionEmbeddingService.FailedRetryInterval"/> has passed.
    /// </summary>
    public DateTimeOffset? EmbeddingFailedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    internal static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DecisionRow>(e =>
        {
            e.ToTable("decisions");
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).ValueGeneratedNever();
            e.Property(r => r.SenderAddress).IsRequired();
            e.Property(r => r.TopicLabel).IsRequired();
            e.Property(r => r.Outcome).IsRequired().HasConversion(new SnakeCaseEnumConverter<DecisionOutcome>());
            e.Property(r => r.Source).IsRequired().HasConversion(new SnakeCaseEnumConverter<SuggestionSource>());
            e.Property(r => r.Embedding).HasColumnType("vector");
            e.HasIndex(r => new { r.SenderAddress, r.Outcome, r.CreatedAt });
            e.HasIndex(r => new { r.EmbeddingModel, r.SenderAddress });
            e.HasIndex(r => new { r.ScopeKey, r.CreatedAt });
            e.HasIndex(r => new { r.ListId, r.CreatedAt });
        });
    }
}
