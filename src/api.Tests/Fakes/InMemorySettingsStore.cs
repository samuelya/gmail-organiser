using GmailOrganiser.Settings;

namespace GmailOrganiser.Tests.Fakes;

/// <summary>An <see cref="ISettingsStore"/> without a database, for unit tests.</summary>
public sealed class InMemorySettingsStore(AppSettings? initial = null) : ISettingsStore
{
    public AppSettings Current { get; set; } = initial ?? new AppSettings { OllamaBaseUrl = "http://ollama.example.com:11434" };

    public Task<AppSettings> GetAsync(CancellationToken ct = default) => Task.FromResult(Current);

    public Task<AppSettings> UpdateAsync(Func<AppSettings, AppSettings> change, CancellationToken ct = default) =>
        Task.FromResult(Current = change(Current));
}
