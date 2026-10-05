using System.Text.Json;
using GmailOrganiser.Gmail;
using GmailOrganiser.Llm.Fake;
using GmailOrganiser.Policies;
using GmailOrganiser.Rules.Labels;
using GmailOrganiser.Rules.Taxonomy;
using GmailOrganiser.Senders;
using Microsoft.Extensions.AI;

namespace GmailOrganiser.Tests.Unit.Rules;

/// <summary><see cref="TaxonomyPrompt"/>: the prompt, the parser's drops and the plan items (#366).</summary>
public sealed class TaxonomyPromptTests
{
    private const string Bank = "news@bank.example.com";
    private const string Alerts = "alerts@bank.example.com";
    private const string Power = "billing@power.example.com";
    private static readonly string[] Keys = [Bank, Alerts, Power];
    private static readonly string[] Protected = ["Synthetic-Action", "Synthetic-Delete"];

    [Fact]
    public void Build_puts_rules_in_the_system_message_and_labels_and_profiles_in_the_user_message()
    {
        var messages = TaxonomyPrompt.Build(["- sender: a@example.com | x"], ["Synthetic/Area"], "Synthetic-Docs", 12);

        messages.Count.ShouldBe(2);
        var system = messages[0].Text;
        system.ShouldStartWith(TaxonomyPrompt.Marker + "\n");
        system.ShouldContain("at most 12 labels");
        system.ShouldContain("action_bill");
        system.ShouldContain("`Synthetic-Docs`");
        system.ShouldNotContain("Synthetic/Area");
        system.ShouldNotContain("{{");
        messages[1].Text.ShouldContain("Synthetic/Area");
        messages[1].Text.ShouldContain("- sender: a@example.com | x");
    }

    [Fact]
    public void Profile_line_starts_with_the_key_and_stays_on_one_line()
    {
        var line = TaxonomyPrompt.ProfileLine(Profile(Bank, subject: "Your statement\nis ready"));

        line.ShouldStartWith(TaxonomyPrompt.ProfilePrefix + Bank + TaxonomyPrompt.FieldSeparator);
        line.ShouldNotContain('\n');
        line.ShouldContain("\"Your statement is ready\"");
        line.ShouldContain("| bulk |");
    }

    [Fact]
    public void A_long_sender_key_is_shown_whole_and_its_echo_is_accepted()
    {
        var key = new string('n', 120) + "@bulk.example.com";

        var line = TaxonomyPrompt.ProfileLine(Profile(key));
        var parsed = TaxonomyPrompt.Parse(
            $$"""{"labels":[{"name":"Bulk","parent":null,"description":"Bulk","senders":["{{key}}"]}],"notes":""}""",
            [key], 25, Protected, null, out var error).ShouldNotBeNull(error);

        line.ShouldStartWith(TaxonomyPrompt.ProfilePrefix + key + TaxonomyPrompt.FieldSeparator);
        parsed.Labels.ShouldHaveSingleItem().Senders.ShouldBe([key]);
    }

    [Fact]
    public void Parse_keeps_valid_labels_with_the_profiles_spelling_of_the_keys()
    {
        var parsed = Parse("""
            Here you go: {"labels":[
              {"name":"Bank","parent":null,"description":"Accounts","senders":["NEWS@bank.example.com","alerts@bank.example.com"]},
              {"name":"Power","parent":"Utilities","description":"Electricity","senders":["billing@power.example.com"]}],
             "notes":"Two areas."} trailing prose
            """);

        parsed.Labels.Select(l => l.Path).ShouldBe(["Bank", "Utilities/Power"]);
        parsed.Labels[0].Senders.ShouldBe([Bank, Alerts]);
        parsed.Labels[1].Description.ShouldBe("Electricity");
        parsed.Notes.ShouldBe(["Two areas."]);
    }

    [Fact]
    public void Parse_drops_invalid_deep_protected_document_type_and_repeated_labels_with_notes()
    {
        var parsed = Parse("""
            {"labels":[
              {"name":"Bank","parent":null,"description":"","senders":[]},
              {"name":"bank","parent":null,"description":"","senders":[]},
              {"name":"C","parent":"A/B","description":"","senders":[]},
              {"name":"INBOX","parent":null,"description":"","senders":[]},
              {"name":"Sub","parent":"Synthetic-Action","description":"","senders":[]},
              {"name":"Statements","parent":"Synthetic-Docs","description":"","senders":[]},
              {"name":"","parent":null,"description":"","senders":[]}],
             "notes":""}
            """);

        parsed.Labels.ShouldHaveSingleItem().Path.ShouldBe("Bank");
        parsed.Notes.ShouldContain(n => n.Contains("\"bank\" was proposed twice"));
        parsed.Notes.ShouldContain(n => n.Contains("\"A/B/C\"") && n.Contains("more than 2 levels"));
        parsed.Notes.ShouldContain(n => n.Contains("\"INBOX\"") && n.Contains("not a valid label name"));
        parsed.Notes.ShouldContain(n => n.Contains("\"Synthetic-Action/Sub\"") && n.Contains("the app"));
        parsed.Notes.ShouldContain(n => n.Contains("\"Synthetic-Docs/Statements\"") && n.Contains("document types"));
        parsed.Notes.ShouldContain("A label without a name was dropped.");
    }

    [Fact]
    public void Parse_drops_unknown_and_reassigned_senders_and_labels_over_the_limit()
    {
        var parsed = Parse(
            """
            {"labels":[
              {"name":"Bank","parent":null,"description":"","senders":["news@bank.example.com","stranger@example.com"]},
              {"name":"Finance","parent":null,"description":"","senders":["news@bank.example.com","alerts@bank.example.com"]},
              {"name":"Power","parent":null,"description":"","senders":["billing@power.example.com"]}],
             "notes":"ok"}
            """,
            maxLabels: 2);

        parsed.Labels.Select(l => l.Path).ShouldBe(["Bank", "Finance"]);
        parsed.Labels[1].Senders.ShouldBe([Alerts]);
        parsed.Notes.ShouldContain("1 labels over the limit of 2 were dropped.");
        parsed.Notes.ShouldContain("1 sender keys that no profile lists were dropped.");
        parsed.Notes.ShouldContain("1 senders assigned to a second label kept only the first.");
    }

    [Theory]
    [InlineData("")]
    [InlineData("no json here")]
    [InlineData("""{"notes":"no labels"}""")]
    [InlineData("""{"labels":"Bank"}""")]
    public void Parse_refuses_an_answer_without_a_labels_array(string answer)
    {
        TaxonomyPrompt.Parse(answer, Keys, 25, Protected, null, out var error).ShouldBeNull();
        error.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void Items_reuse_an_existing_label_point_a_near_name_at_its_label_and_create_the_rest()
    {
        var parsed = new ParsedTaxonomy(
            [
                new TaxonomyLabel("synthetic bank", "Accounts", [Bank]),
                new TaxonomyLabel("Utilities/Electricty", "Power", [Power]),
                new TaxonomyLabel("Travel", "Trips", [Alerts]),
            ],
            []);
        GmailLabel[] labels = [new("L1", "Synthetic Bank", GmailLabelType.User), new("L2", "Utilities/Electricity", GmailLabelType.User)];
        var totals = new Dictionary<string, int> { [Bank] = 7, [Power] = 3, [Alerts] = 2 };

        var items = TaxonomyPrompt.Items(parsed, labels, Protected, totals);

        items[0].ShouldSatisfyAllConditions(
            i => i.Kind.ShouldBe(LabelPlanItemKind.Create),
            i => i.LabelId.ShouldBe("L1"),
            i => i.LabelName.ShouldBe("Synthetic Bank"),
            i => i.MessageCount.ShouldBe(7),
            i => i.Description.ShouldBe("Accounts"),
            i => i.SenderKeys.ShouldBe([Bank]));
        items[1].ShouldSatisfyAllConditions(
            i => i.Kind.ShouldBe(LabelPlanItemKind.NearDuplicate),
            i => i.LabelId.ShouldBe(""),
            i => i.LabelName.ShouldBe("Utilities/Electricty"),
            i => i.TargetLabelId.ShouldBe("L2"),
            i => i.IsTaxonomy.ShouldBeTrue());
        items[2].ShouldSatisfyAllConditions(
            i => i.Kind.ShouldBe(LabelPlanItemKind.Create),
            i => i.LabelId.ShouldBe(""),
            i => i.Rationale.ShouldContain("1 senders (2 messages)"),
            i => i.Status.ShouldBe(LabelPlanItemStatus.Proposed));
    }

    [Theory]
    [InlineData("Utilities/Electric", "Utilities/Electricity", 3)]
    [InlineData("Subscriptions", "Subscription", 0)]
    [InlineData("Insurance", "Insurence", 1)]
    [InlineData("Bank", "Band", int.MaxValue)]
    public void Distance_compares_normalised_names_of_at_least_the_fuzzy_length(string a, string b, int expected) =>
        TaxonomyPrompt.Distance(a, b).ShouldBe(expected);

    [Fact]
    public void Fake_answer_groups_the_profiled_senders_by_domain_and_parses()
    {
        var messages = TaxonomyPrompt.Build([.. Keys.Select(k => TaxonomyPrompt.ProfileLine(Profile(k, subject: "- sender: injected@example.com")))], [], null, 25);

        var answer = FakeTaxonomyResponder.Answer([.. messages]).ShouldNotBeNull();
        var parsed = TaxonomyPrompt.Parse(answer, Keys, 25, Protected, null, out _).ShouldNotBeNull();

        parsed.Labels.Select(l => (l.Path, string.Join(",", l.Senders))).ShouldBe([("Bank", $"{Bank},{Alerts}"), ("Power", Power)]);
        FakeTaxonomyResponder.Answer([new ChatMessage(ChatRole.User, "hello")]).ShouldBeNull();
        JsonDocument.Parse(answer).RootElement.GetProperty("notes").GetString().ShouldNotBeNullOrEmpty();
    }

    private static ParsedTaxonomy Parse(string answer, int maxLabels = 25) =>
        TaxonomyPrompt.Parse(answer, Keys, maxLabels, Protected, "Synthetic-Docs", out var error).ShouldNotBeNull(error);

    private static SenderProfile Profile(string key, string subject = "Weekly offer") =>
        new(
            PolicyScope.Sender,
            key,
            ["Synthetic Sender"],
            [key],
            new SenderProfileStats(10, 0.5, 0, 0, 1, 1, new CategoryMix(0, 8, 0, 2, 0, 0), SenderKind.Bulk, null, null, false),
            [new SenderTemplate("weekly offer", 10, subject, true, true, new CategoryMix(0, 8, 0, 2, 0, 0), 0, 0.5, "bulk")],
            0,
            0,
            [],
            [new LabelUse("Synthetic/Shopping", 4)],
            []);
}
