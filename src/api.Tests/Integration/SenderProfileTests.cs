using System.Net;
using System.Net.Http.Json;
using GmailOrganiser.Fetch;
using GmailOrganiser.Gmail;
using GmailOrganiser.Gmail.Fake;
using GmailOrganiser.Jobs;
using GmailOrganiser.Policies;
using GmailOrganiser.Rules;
using GmailOrganiser.Senders;
using GmailOrganiser.Tests.Fakes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace GmailOrganiser.Tests.Integration;

/// <summary><see cref="SenderProfileBuilder"/> over Postgres and the fake Gmail, through the profile endpoints (#355).</summary>
[Collection(PostgresCollection.Name)]
public sealed class SenderProfileTests(ApiFactory factory, PostgresFixture postgres) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private const string News = "news@example.com";
    private const string Relay = "news.relay@relay.example.com";
    private const string ListId = "Offers <offers.example.com>";
    private const string BodyMarker = "SYNTHETIC-PROFILE-BODY";
    private static readonly DateTimeOffset Newest = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

    private WebApplicationFactory<Program> host = null!;
    private string labelId = "";
    private string labelName = "";

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

        await using var db = postgres.CreateDbContext();
        await db.SenderPolicies.ExecuteDeleteAsync(Ct);
        await db.Messages.ExecuteDeleteAsync(Ct);
        await db.Senders.ExecuteDeleteAsync(Ct);
        await db.Settings.ExecuteDeleteAsync(Ct);

        var gmail = host.Services.GetRequiredService<FakeGmailClient>();
        var label = (await gmail.ListLabelsAsync(Ct)).First(l => l.Type == GmailLabelType.User);
        (labelId, labelName) = (label.Id, label.Name);

        var rows = new List<MessageRow>();
        // 12 promotions from one template, newest first; the newest comes through the relay address.
        for (var i = 0; i < 12; i++)
        {
            var offer = Row($"offer{i:D2}", i == 0 ? Relay : News, $"Weekly offer {100 + i}", i, MessageCategory.Promotions, [FilterSpec.Unread]);
            offer.ListId = ListId;
            offer.ListUnsubscribe = "<https://unsubscribe.example.com/x>";
            offer.Precedence = "bulk";
            offer.FromName = "Example News";
            rows.Add(offer);
        }

        // 3 receipts with attachments and a user label: the second category of a mixed sender.
        for (var i = 0; i < 3; i++)
        {
            var receipt = Row($"receipt{i}", News, $"Your receipt no. {500 + i}", 20 + i, MessageCategory.Updates, [labelId]);
            receipt.HasAttachment = true;
            rows.Add(receipt);
        }

        // 11 one-off subjects push the template count past the cap.
        rows.AddRange(Enumerable.Range(0, 11).Select(i => Row($"oneoff{i:D2}", News, $"Topic {(char)('a' + i)} news", 30 + i, MessageCategory.Updates, [])));
        var deleted = Row("deleted", News, "Weekly offer 999", 0, MessageCategory.Promotions, []);
        deleted.DeletedInGmail = true;
        rows.Add(deleted);
        rows.Add(Row("billing", "billing@example.com", "Invoice 7", 1, MessageCategory.Updates, []));
        db.Messages.AddRange(rows);
        rows.ForEach(r => gmail.AddMessage(new FakeMessage(r.Id, r.ThreadId, r.FromAddress, r.Subject ?? "", r.InternalDate, r.LabelIds, BodyText: $"{BodyMarker} {r.Id}")));

        db.Senders.AddRange(
            Sender(News, News),
            Sender(Relay, News),
            Sender("billing@example.com", "billing@example.com"));
        db.SenderPolicies.AddRange(
            Policy("billing@example.com", PolicyStatus.Approved),
            Policy("other@example.com", PolicyStatus.Proposed),
            Policy(News, PolicyStatus.Approved));
        await db.SaveChangesAsync(Ct);
    }

    public async ValueTask DisposeAsync() => await host.DisposeAsync();

    [Fact]
    public async Task Profile_of_a_sender_has_stats_capped_templates_labels_and_hints_without_bodies()
    {
        var p = await GetAsync($"/api/senders/{News}/profile");

        p.Scope.ShouldBe("sender");
        p.ScopeKey.ShouldBe(News);
        p.Addresses.ShouldBe([News, Relay]);
        p.DisplayNames.ShouldBe(["Example News"]);
        p.Stats.Total.ShouldBe(26);
        p.Stats.UnreadRatio.ShouldBe(Math.Round(12 / 26.0, 3));
        p.Stats.ListUnsubscribeRatio.ShouldBe(Math.Round(12 / 26.0, 3));
        p.Stats.CategoryMix.ShouldBe(new CategoryMixDto(0, 12, 0, 14, 0, 0));
        p.Stats.Kind.ShouldBe("mixed");
        p.Stats.LastSeen.ShouldBe(Newest);
        p.Stats.FirstSeen.ShouldBe(Newest.AddHours(-40));
        p.Stats.Allowlisted.ShouldBeFalse();

        p.Templates.Count.ShouldBe(SenderProfileBuilder.ProfileMaxTemplates);
        p.OtherTemplates.ShouldBe(3);
        p.OtherTemplateMessages.ShouldBe(3);
        var offer = p.Templates[0];
        offer.Template.ShouldBe("weekly offer #");
        offer.Count.ShouldBe(12);
        offer.ExampleSubject.ShouldBe("Weekly offer 100");
        offer.ListIdPresent.ShouldBeTrue();
        offer.ListUnsubscribePresent.ShouldBeTrue();
        offer.Precedence.ShouldBe("bulk");
        offer.UnreadRatio.ShouldBe(1);
        p.Templates[1].Template.ShouldBe("your receipt #");
        p.Templates[1].AttachmentRatio.ShouldBe(1);
        p.Templates[1].ListIdPresent.ShouldBeFalse();

        p.LabelsInUse.ShouldBe([new LabelUse(labelName, 3)]);
        p.ApprovedPolicyHints.Select(h => h.ScopeKey).ShouldBe(["billing@example.com"]);
        p.Bodies.ShouldBeEmpty();
        p.PromptText.ShouldContain("+3 other subjects (3 messages)");
    }

    [Fact]
    public async Task A_relay_address_resolves_to_its_canonical_sender()
    {
        (await GetAsync($"/api/senders/{Uri.EscapeDataString(Relay.ToUpperInvariant())}/profile")).ScopeKey.ShouldBe(News);
    }

    [Fact]
    public async Task Bodies_are_the_newest_of_the_top_template_and_of_the_first_other_category()
    {
        var p = await GetAsync($"/api/senders/{News}/profile?includeBodies=true");

        p.Bodies.Select(b => b.Template).ShouldBe(["weekly offer #", "your receipt #"]);
        p.Bodies[0].Text.ShouldBe($"{BodyMarker} offer00");
        p.Bodies[1].Text.ShouldBe($"{BodyMarker} receipt0");
        p.PromptText.ShouldContain($"{BodyMarker} receipt0");
    }

    [Fact]
    public async Task Gmail_rate_limits_skip_labels_and_bodies_but_keep_the_profile()
    {
        host.Services.GetRequiredService<FakeGmailClient>().FailNext(HttpStatusCode.TooManyRequests, 10);

        var p = await GetAsync($"/api/senders/{News}/profile?includeBodies=true");

        p.Stats.Total.ShouldBeGreaterThan(0);
        p.Templates.ShouldNotBeEmpty();
        p.LabelsInUse.ShouldBeEmpty();
        p.Bodies.ShouldBeEmpty();
    }

    [Fact]
    public async Task List_profile_matches_the_normalised_list_id()
    {
        var p = await GetAsync($"/api/senders/profile?listId={Uri.EscapeDataString("  " + ListId.ToUpperInvariant())}");

        p.Scope.ShouldBe("list");
        p.ScopeKey.ShouldBe(ListId.ToLowerInvariant());
        p.Stats.Total.ShouldBe(12);
        p.Stats.Kind.ShouldBe("bulk");
        p.Templates.ShouldHaveSingleItem().Count.ShouldBe(12);
        p.ApprovedPolicyHints.Select(h => h.ScopeKey).ShouldBe(["billing@example.com", News]);
    }

    [Theory]
    [InlineData("/api/senders/nobody@example.com/profile", HttpStatusCode.NotFound)]
    [InlineData("/api/senders/not-an-address/profile", HttpStatusCode.BadRequest)]
    [InlineData("/api/senders/profile?listId=unknown.example.com", HttpStatusCode.NotFound)]
    [InlineData("/api/senders/profile", HttpStatusCode.BadRequest)]
    public async Task Unknown_or_invalid_input_is_a_problem(string path, HttpStatusCode status)
    {
        (await host.CreateClient().GetAsync(path, Ct)).StatusCode.ShouldBe(status);
    }

    private async Task<SenderProfileDto> GetAsync(string path)
    {
        var response = await host.CreateClient().GetAsync(path, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<SenderProfileDto>(Ct))!;
    }

    private static MessageRow Row(string id, string from, string subject, int hoursAgo, MessageCategory category, string[] labels) => new()
    {
        Id = id,
        ThreadId = "t-" + id,
        FromAddress = from,
        CanonicalAddress = from == Relay ? News : from,
        CanonicalDomain = "example.com",
        Subject = subject,
        InternalDate = Newest.AddHours(-hoursAgo),
        LabelIds = labels,
        Category = category,
        FetchedAt = Newest,
        UpdatedAt = Newest,
    };

    private static SenderRow Sender(string address, string canonical) => new()
    {
        Address = address,
        Domain = address[(address.IndexOf('@') + 1)..],
        CanonicalAddress = canonical,
        CanonicalDomain = "example.com",
        IsRelay = address != canonical,
        UpdatedAt = Newest,
    };

    private static SenderPolicyRow Policy(string address, PolicyStatus status) => new()
    {
        Id = Guid.NewGuid(),
        Scope = PolicyScope.Sender,
        ScopeKey = address,
        TopicLabel = "Example/Topic",
        Action = PolicyAction.Archive,
        Confidence = 0.9,
        Reason = "synthetic",
        Status = status,
        CreatedAt = Newest,
    };
}
