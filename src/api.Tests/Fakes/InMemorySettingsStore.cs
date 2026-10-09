using GmailOrganiser.Settings;

namespace GmailOrganiser.Tests.Fakes;

/// <summary>An <see cref="ISettingsStore"/> without a database, for unit tests.</summary>
public sealed class InMemorySettingsStore(AppSettings? initial = null) : ISettingsStore
{
    public AppSettings Current { get; set; } = initial ?? new AppSettings { OllamaBaseUrl = "http://ollama.example.com:11434" };

    public Task<AppSettings> GetAsync(CancellationToken ct = default) => Task.FromResult(Current);

    /// <summary>How many times <see cref="UpdateAsync"/> ran (each is a locked write in the real store).</summary>
    public int UpdateCount { get; private set; }

    public Task<AppSettings> UpdateAsync(Func<AppSettings, AppSettings> change, CancellationToken ct = default)
    {
        UpdateCount++;
        return Task.FromResult(Current = change(Current));
    }
}
