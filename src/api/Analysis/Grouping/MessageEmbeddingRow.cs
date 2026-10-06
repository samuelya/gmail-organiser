using GmailOrganiser.Fetch;
using Microsoft.EntityFrameworkCore;
using Pgvector;

namespace GmailOrganiser.Analysis.Grouping;

/// <summary>
/// A message's embedding for the Auto grouping's clusters (#114), one row per message. A row of another
/// <see cref="Model"/> is re-embedded and overwritten. Untyped <c>vector</c> without an index: the clustering runs in
/// memory on a run's loaded vectors, never as a similarity query.
/// </summary>
public sealed class MessageEmbeddingRow
{
    public string MessageId { get; set; } = "";
    public string Model { get; set; } = "";
    public int Dimension { get; set; }
    public Vector Embedding { get; set; } = new(new float[] { 0f });
    public DateTimeOffset CreatedAt { get; set; }

    internal static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MessageEmbeddingRow>(e =>
        {
            e.ToTable("message_embeddings");
            e.HasKey(r => r.MessageId);
            e.Property(r => r.MessageId).ValueGeneratedNever();
            e.Property(r => r.Model).IsRequired();
            e.Property(r => r.Embedding).IsRequired().HasColumnType("vector");
            e.HasOne<MessageRow>().WithMany().HasForeignKey(r => r.MessageId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
