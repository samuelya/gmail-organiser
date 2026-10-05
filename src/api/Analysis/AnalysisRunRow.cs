using GmailOrganiser.Data;
using GmailOrganiser.Settings;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Analysis;

/// <summary>Which messages a run picks.</summary>
public enum AnalysisScope
{
    Inbox,
    All,
    Sender,
    Messages,

    /// <summary>Not-analysed mail that already carries at least one user label (the labelled phase).</summary>
    Labelled,
}

/// <summary>What a run writes: <c>suggestions</c> (analyse) or <c>suggestion_alternatives</c> next to them (compare).</summary>
public enum AnalysisRunKind
{
    Analyse,

    /// <summary>Re-analyses existing suggestions with the current prompt, without the memory short-circuit (#248).</summary>
    Compare,
}

public enum AnalysisRunStatus
{
    Queued,
    Running,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>A user-started analysis run (<c>analysis_runs</c>) with its counters.</summary>
public sealed class AnalysisRunRow
{
    public Guid Id { get; set; }
    public Guid? JobId { get; set; }
    public AnalysisRunKind Kind { get; set; }
    public AnalysisScope Scope { get; set; }

    /// <summary>Set for <see cref="AnalysisScope.Sender"/>.</summary>
    public string? SenderAddress { get; set; }

    /// <summary>Set for <see cref="AnalysisScope.Messages"/>.</summary>
    public string[]? MessageIds { get; set; }

    /// <summary>Emails to cover (LLM, derived and memory suggestions all count).</summary>
    public int RequestedCount { get; set; }
    public AnalysisGroupingMode GroupingMode { get; set; }
    public AnalysisRunStatus Status { get; set; }
    public int MessagesCovered { get; set; }
    public int MessagesLlm { get; set; }
    public int MessagesDerived { get; set; }
    public int MessagesFromMemory { get; set; }
    public int LlmCalls { get; set; }
    public int Groups { get; set; }
    public int MixedGroups { get; set; }
    public int FailedMessages { get; set; }

    /// <summary>Selected messages the run does not analyse (approved, applied, deleted or unknown ids).</summary>
    public int SkippedMessages { get; set; }
    public int AttachmentsConverted { get; set; }
    public int AttachmentsSkipped { get; set; }

    /// <summary>Summed over the run's model calls (#353); 0 when the model reports no usage.</summary>
    public long PromptTokens { get; set; }
    public long CompletionTokens { get; set; }
    public long LlmMilliseconds { get; set; }

    /// <summary>Model calls whose prompt filled at least 90 % of <c>num_ctx</c>, so Ollama likely truncated it.</summary>
    public int NearContextLimit { get; set; }
    public string? Model { get; set; }
    public string? PromptVersion { get; set; }

    /// <summary>The document-type parent pinned when the run first starts (null: off), so a resumed run keeps it.</summary>
    public string? DocumentTypeParent { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }

    internal static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AnalysisRunRow>(e =>
        {
            e.ToTable("analysis_runs");
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).ValueGeneratedNever();
            e.Property(r => r.Kind).IsRequired().HasDefaultValueSql("'analyse'").HasConversion(new SnakeCaseEnumConverter<AnalysisRunKind>());
            e.Property(r => r.Scope).IsRequired().HasConversion(new SnakeCaseEnumConverter<AnalysisScope>());
            e.Property(r => r.GroupingMode).IsRequired().HasConversion(new SnakeCaseEnumConverter<AnalysisGroupingMode>());
            e.Property(r => r.Status).IsRequired().HasConversion(new SnakeCaseEnumConverter<AnalysisRunStatus>());
            e.HasIndex(r => new { r.Status, r.CreatedAt });
        });
    }
}
