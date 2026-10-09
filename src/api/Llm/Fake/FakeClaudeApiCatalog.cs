using GmailOrganiser.Llm.ClaudeApi;

namespace GmailOrganiser.Llm.Fake;

/// <summary>The Claude API catalog of <c>LLM_FAKE=true</c>: one model, listed only when a key is saved, no HTTP.</summary>
public sealed class FakeClaudeApiCatalog(ClaudeApiKeyService keys) : IClaudeApiCatalog
{
    public const string Model = "fake-claude";

    public async Task<ClaudeApiModelsDto> ListModelsAsync(CancellationToken ct = default) =>
        string.IsNullOrWhiteSpace(await keys.GetAsync(ct))
            ? ClaudeApiModelsDto.NoKey
            : ClaudeApiModelsDto.Listed([new ClaudeApiModelDto(Model, "Fake Claude", null)]);
}
