using System.Net.Http.Json;
using GmailOrganiser.Analysis.Prompts;
using GmailOrganiser.Tests.Fakes;

namespace GmailOrganiser.Tests.Integration;

[Collection(PostgresCollection.Name)]
public sealed class AnalysisPromptEndpointsTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Default_prompt_returns_the_built_in_template_and_version()
    {
        var dto = await factory.CreateClient().GetFromJsonAsync<PromptTemplateDto>(
            "/api/analysis/prompt/default", TestContext.Current.CancellationToken);

        dto.ShouldNotBeNull().Version.ShouldBe(PromptTemplate.BuiltInVersion);
        dto.Template.ShouldBe(PromptTemplate.BuiltIn.Text);
        dto.Template.ShouldContain(PromptTemplate.EmailsPlaceholder);
    }
}
