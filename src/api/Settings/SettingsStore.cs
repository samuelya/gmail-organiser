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
        StoreChanges(stored, before, after);

        row.Document = stored.ToJsonString(Json);
        row.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Effective(stored);
    }

    /// <summary>
    /// The write side of <see cref="Overlay"/>: stores only the leaf values that changed, recursing into nested blocks,
    /// so values the user never changed keep following the defaults. Lists of <c>{"type": …}</c> entries are stored per type.
    /// </summary>
    private static void StoreChanges(JsonObject stored, JsonObject before, JsonObject after)
    {
        foreach (var (name, value) in after)
        {
            var previous = before[name];
            if (JsonNode.DeepEquals(value, previous))
            {
                continue;
            }

            if (value is JsonObject block && previous is JsonObject previousBlock)
            {
                if (stored[name] is not JsonObject storedBlock)
                {
                    stored[name] = storedBlock = [];
                }

                StoreChanges(storedBlock, previousBlock, block);
            }
            else if (value is JsonArray list && previous is JsonArray previousList && TypeKeys(list) is { } keys
                && TypeKeys(previousList) is { } previousKeys)
            {
                if (stored[name] is not JsonArray entries)
                {
                    stored[name] = entries = [];
                }

                foreach (var (type, entry) in keys.Where(k => !JsonNode.DeepEquals(k.Value, previousKeys.GetValueOrDefault(k.Key))))
                {
                    // The reader takes the first entry per type, so the new entry replaces any saved one.
                    entries.RemoveAll(e => e is JsonObject o && o["type"] is JsonValue t
                        && t.GetValueKind() == JsonValueKind.String && t.GetValue<string>() == type);
                    entries.Add(entry.DeepClone());
                }
            }
            else
            {
                stored[name] = value?.DeepClone();
            }
        }
    }

    /// <summary>The entries of a list keyed by a string <c>type</c>, or <c>null</c> if it isn't such a list.</summary>
    private static Dictionary<string, JsonNode>? TypeKeys(JsonArray list)
    {
        var keys = new Dictionary<string, JsonNode>();
        foreach (var entry in list)
        {
            if (entry is not JsonObject o || o["type"] is not JsonValue type || type.GetValueKind() != JsonValueKind.String
                || !keys.TryAdd(type.GetValue<string>(), o))
            {
                return null;
            }
        }

        return keys;
    }

    private AppSettings Effective(JsonObject stored)
    {
        var defaults = AppSettings.Defaults(env.Value);
        var merged = JsonSerializer.SerializeToNode(defaults, Json)!.AsObject();

        Overlay(merged, merged, stored, "");
        var settings = TryDeserialize(merged) ?? defaults;
        var envClientId = env.Value.GoogleClientId;
        return string.IsNullOrWhiteSpace(envClientId) ? settings : settings with { GoogleClientId = envClientId.Trim() };
    }

    /// <summary>
    /// Applies saved values one by one onto <paramref name="target"/> (a node of <paramref name="root"/>), recursing into
    /// nested blocks, so a single unreadable value falls back to its default and a block missing a value keeps the default.
    /// </summary>
    private void Overlay(JsonObject root, JsonObject target, JsonObject stored, string path)
    {
        foreach (var (name, value) in stored)
        {
            if (!target.ContainsKey(name) || (value is null && target[name] is not null))
            {
                continue; // unknown property, or null for a setting that must have a value
            }

            if (value is JsonObject block && target[name] is JsonObject defaults)
            {
                Overlay(root, defaults, block, $"{path}{name}.");
                continue;
            }

            var previous = target[name]?.DeepClone();
            target[name] = value?.DeepClone();
            if (TryDeserialize(root) is null)
            {
                target[name] = previous;
                logger.LogWarning("Saved setting {Setting} is unreadable; using its default", path + name);
            }
        }
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
