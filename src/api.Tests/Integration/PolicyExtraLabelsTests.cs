using System.Net;
using System.Net.Http.Json;
using GmailOrganiser.Analysis;
using GmailOrganiser.Common;
using GmailOrganiser.Gmail;
using GmailOrganiser.Gmail.Fake;
using GmailOrganiser.Jobs;
using GmailOrganiser.Policies;
using GmailOrganiser.Review;
using GmailOrganiser.Settings;
using GmailOrganiser.Tests.Fakes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace GmailOrganiser.Tests.Integration;

/// <summary>Additional labels on sender policies and their rules (#530): edit validation, canonical storage and DTOs.</summary>
[Collection(PostgresCollection.Name)]
public sealed class PolicyExtraLabelsTests(ApiFactory factory, PostgresFixture postgres) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private const string Existing = "Synthetic Extra/Known";
    private const string Topic = "Example/Topic";
    private const string RuleTopic = "Example/Shop";
    private static readonly DateTimeOffset Created = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly Guid Single = Guid.NewGuid();
    private static readonly Guid Mixed = Guid.NewGuid();
    private static readonly Guid MixedRule = Guid.NewGuid();

    private WebApplicationFactory<Program> host = null!;
    private HttpClient client = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        host = factory.WithWebHostBuilder(b => b.UseSetting("GMAIL_FAKE", "true").ConfigureTestServices(services =>
        {
            var retry = new GmailRetryPolicy(Options.Create(new GmailOptions { MaxRetryAttempts = 1 }), TimeProvider.System);
            services.AddSingleton(sp => new FakeGmailClient(sp.GetRequiredService<FakeTokenStore>(), [], retry));
            services.AddScoped<IGmailClient>(sp => sp.GetRequiredService<FakeGmailClient>());
            services.Remove(services.Single(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(JobRunner)));
        }));
        client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");

        await using var db = postgres.CreateDbContext();
        await db.SenderPolicies.ExecuteDeleteAsync(Ct);
        await db.Settings.ExecuteDeleteAsync(Ct);
        var single = Policy(Single, "single@example.com", PolicyStatus.Approved);
        var mixed = Policy(Mixed, "mixed@example.com", PolicyStatus.Proposed);
        mixed.IsMixed = true;
        mixed.TopicLabel = null;
        mixed.Rules =
        [
            new SenderPolicyRuleRow
            {
                Id = MixedRule,
                Name = "Synthetic rule",
                Match = new RuleMatch { SubjectContains = "deal" },
                TopicLabel = RuleTopic,
                Action = PolicyAction.Archive,
                Status = PolicyStatus.Proposed,
                Source = PolicyRuleSource.Llm,
                Reason = "synthetic",
                CreatedAt = Created,
            },
        ];
        db.SenderPolicies.AddRange(single, mixed);
        await db.SaveChangesAsync(Ct);

        await host.Services.GetRequiredService<FakeGmailClient>().CreateLabelAsync(Existing, Ct);
        host.Services.GetRequiredService<LabelCatalog>().Invalidate();
    }

    public async ValueTask DisposeAsync()
    {
        client.Dispose();
        await host.DisposeAsync();
    }

    [Fact]
    public async Task Policies_and_rules_start_without_extras()
    {
        var detail = await DetailAsync(Mixed);

        detail.Policy.ExtraLabels.ShouldBeEmpty();
        detail.Rules.Single().ExtraLabels.ShouldBeEmpty();
        (await ListAsync("?status=approved")).Items.Single().ExtraLabels.ShouldBeEmpty();
    }

    [Fact]
    public async Task Edit_stores_extras_canonical_in_gmails_spelling_and_asks_to_reapply()
    {
        var response = await PutAsync(Single, new EditPolicyRequest(Topic, null, null, null, "archive", false, [],
            [" synthetic extra/KNOWN ", "Fresh", "fresh", "Alpha"]));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = (await response.Content.ReadFromJsonAsync<EditPolicyResponse>(Ct)).ShouldNotBeNull();
        body.Reapply.ShouldBeTrue();
        body.Policy.Policy.ExtraLabels.ShouldBe(["Alpha", "Fresh", Existing]);
        (await ListAsync("?status=approved")).Items.Single().ExtraLabels.ShouldBe(["Alpha", "Fresh", Existing]);
        await using (var db = postgres.CreateDbContext())
        {
            (await db.SenderPolicies.AsNoTracking().SingleAsync(p => p.Id == Single, Ct)).ExtraLabels.ShouldBe(["Alpha", "Fresh", Existing]);
        }

        // The edit replaces the whole policy: no list clears them.
        (await PutAsync(Single, new EditPolicyRequest(Topic, null, null, null, "archive", false, []))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await DetailAsync(Single)).Policy.ExtraLabels.ShouldBeEmpty();
    }

    [Fact]
    public async Task Edit_stores_rule_extras()
    {
        var response = await PutAsync(Mixed, new EditPolicyRequest(null, null, null, null, "keep", true,
            [Rule(MixedRule, ["Beta", "alpha"])]));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = (await response.Content.ReadFromJsonAsync<EditPolicyResponse>(Ct)).ShouldNotBeNull();
        body.Reapply.ShouldBeFalse();
        body.Policy.Rules.Single().ExtraLabels.ShouldBe(["alpha", "Beta"]);
        await using var db = postgres.CreateDbContext();
        (await db.SenderPolicyRules.AsNoTracking().SingleAsync(r => r.Id == MixedRule, Ct)).ExtraLabels.ShouldBe(["alpha", "Beta"]);
    }

    [Fact]
    public async Task Edit_refuses_invalid_extras_naming_each_entry_and_saves_nothing()
    {
        var settings = await host.Services.GetRequiredService<ISettingsStore>().GetAsync(Ct);
        var many = Enumerable.Range(1, ExtraLabelList.Max + 1).Select(i => $"Extra {i}").ToArray();

        var response = await PutAsync(Mixed, new EditPolicyRequest(null, null, null, null, "keep", true,
            [Rule(MixedRule, [" ", RuleTopic.ToLowerInvariant(), settings.DeleteLabelName]), Rule(null, many)]));

        var errors = await ErrorsAsync(response);
        errors["rules[0].extraLabels"].Length.ShouldBe(3);
        errors["rules[0].extraLabels"][0].ShouldStartWith("Entry 1:");
        errors["rules[0].extraLabels"][1].ShouldContain("topic label");
        errors["rules[1].extraLabels"].ShouldBe([$"At most {ExtraLabelList.Max} additional labels."]);

        var single = await ErrorsAsync(await PutAsync(Single, new EditPolicyRequest(Topic, null, null, null, "archive", false, [],
            ["INBOX", settings.ActionLabelName])));
        single[ExtraLabelList.Field].Length.ShouldBe(2);
        single[ExtraLabelList.Field][0].ShouldStartWith("Entry 1 'INBOX':");

        await using var db = postgres.CreateDbContext();
        (await db.SenderPolicyRules.AsNoTracking().CountAsync(r => r.PolicyId == Mixed, Ct)).ShouldBe(1);
        (await db.SenderPolicies.AsNoTracking().SingleAsync(p => p.Id == Single, Ct)).Edited.ShouldBeFalse();
    }

    [Fact]
    public async Task A_mixed_default_takes_no_extras()
    {
        var errors = await ErrorsAsync(await PutAsync(Mixed, new EditPolicyRequest(null, null, null, null, "keep", true,
            [Rule(MixedRule, null)], ["Alpha"])));

        errors[ExtraLabelList.Field].Single().ShouldContain("mixed");
    }

    private Task<HttpResponseMessage> PutAsync(Guid id, EditPolicyRequest request) =>
        client.PutAsJsonAsync($"/api/policies/{id}", request, Ct);

    private static async Task<IDictionary<string, string[]>> ErrorsAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        return (await response.Content.ReadFromJsonAsync<HttpValidationProblemDetails>(Ct)).ShouldNotBeNull().Errors;
    }

    private async Task<PagedDto<SenderPolicyDto>> ListAsync(string query) =>
        (await client.GetFromJsonAsync<PagedDto<SenderPolicyDto>>($"/api/policies{query}", Ct)).ShouldNotBeNull();

    private async Task<SenderPolicyDetailDto> DetailAsync(Guid id) =>
        (await client.GetFromJsonAsync<SenderPolicyDetailDto>($"/api/policies/{id}", Ct)).ShouldNotBeNull();

    private static EditPolicyRuleRequest Rule(Guid? id, string[]? extras) =>
        new(id, "Synthetic rule", new RuleMatchDto(SubjectContains: "deal"), RuleTopic, null, null, null, "archive", extras);

    private static SenderPolicyRow Policy(Guid id, string address, PolicyStatus status) => new()
    {
        Id = id,
        Scope = PolicyScope.Sender,
        ScopeKey = address,
        TopicLabel = Topic,
        Action = PolicyAction.Archive,
        Confidence = 0.9,
        Reason = "synthetic",
        Status = status,
        CreatedAt = Created,
    };
}
