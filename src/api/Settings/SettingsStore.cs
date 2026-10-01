using System.Text.Json;
using System.Text.Json.Nodes;
using GmailOrganiser.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GmailOrganiser.Settings;

public interface ISettingsStore
{
    /// <summary>The effective settings: saved values over <c>.env</c> defaults over code defaults.</summary>
    Task<AppSettings> GetAsync(CancellationToken ct = default);

    /// <summary>Applies <paramref name="change"/> to the effective settings and saves what changed.</summary>
    Task<AppSettings> UpdateAsync(Func<AppSettings, AppSettings> change, CancellationToken ct = default);
}

/// <summary>
/// Stores only the values the user saved, so a later <c>.env</c> change still applies to anything never saved.
/// <c>GOOGLE_CLIENT_ID</c> in <c>.env</c> always wins over the saved client ID.
/// </summary>
public sealed class SettingsStore(
    AppDbContext db,
    IOptions<SettingsEnvOptions> env,
    TimeProvider time,
    ILogger<SettingsStore> logger) : ISettingsStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<AppSettings> GetAsync(CancellationToken ct = default)
    {
        var row = await db.Settings.AsNoTracking().SingleOrDefaultAsync(r => r.Id == SettingsRow.SingletonId, ct);
        return Effective(Parse(row?.Document));
    }

    public async Task<AppSettings> UpdateAsync(Func<AppSettings, AppSettings> change, CancellationToken ct = default)
    {
        // Read-modify-write under a row lock: ensure the singleton exists (concurrent first inserts
        // wait on the PK and then do nothing), then lock it so concurrent updates apply one after another.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync(
            $"INSERT INTO settings (id, document) VALUES ({SettingsRow.SingletonId}, '{{}}'::jsonb) ON CONFLICT (id) DO NOTHING",
            ct);
        var row = (await db.Settings
            .FromSql($"SELECT * FROM settings WHERE id = {SettingsRow.SingletonId} FOR UPDATE")
            .ToListAsync(ct)).Single();
        var stored = Parse(row.Document);

        var before = JsonSerializer.SerializeToNode(Effective(stored), Json)!.AsObject();
        var after = JsonSerializer.SerializeToNode(change(Effective(stored)), Json)!.AsObject();
        foreach (var (name, value) in after)
        {
            if (!JsonNode.DeepEquals(value, before[name]))
            {
                stored[name] = value?.DeepClone();
            }
        }

        row.Document = stored.ToJsonString(Json);
        row.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Effective(stored);
    }

    private AppSettings Effective(JsonObject stored)
    {
        var defaults = AppSettings.Defaults(env.Value);
        var merged = JsonSerializer.SerializeToNode(defaults, Json)!.AsObject();

        // Apply saved values one by one, so a single unreadable value falls back to its default.
        foreach (var (name, value) in stored)
        {
            if (!merged.ContainsKey(name) || (value is null && merged[name] is not null))
            {
                continue; // unknown property, or null for a setting that must have a value
            }

            var candidate = merged.DeepClone().AsObject();
            candidate[name] = value?.DeepClone();
            if (TryDeserialize(candidate) is not null)
            {
                merged = candidate;
            }
            else
            {
                logger.LogWarning("Saved setting {Setting} is unreadable; using its default", name);
            }
        }

        var settings = TryDeserialize(merged) ?? defaults;
        var envClientId = env.Value.GoogleClientId;
        return string.IsNullOrWhiteSpace(envClientId) ? settings : settings with { GoogleClientId = envClientId.Trim() };
    }

    private static AppSettings? TryDeserialize(JsonObject node)
    {
        try
        {
            return node.Deserialize<AppSettings>(Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private JsonObject Parse(string? document)
    {
        if (string.IsNullOrWhiteSpace(document))
        {
            return [];
        }

        try
        {
            return JsonNode.Parse(document) as JsonObject ?? [];
        }
        catch (JsonException)
        {
            logger.LogWarning("Saved settings document is not a JSON object; using defaults");
            return [];
        }
    }
}
