using GmailOrganiser.Rules;
using GmailOrganiser.Settings;

namespace GmailOrganiser.Tests.Unit;

/// <summary>Validation of the <c>appsScript</c> block and the generated CONFIG block (#210).</summary>
public sealed class AppsScriptSettingsTests
{
    private static Dictionary<string, string[]> Validate(AppsScriptSettings appsScript) =>
        SettingsValidation.Validate(new UpdateSettingsRequest(null, null, null, null, AppsScript: appsScript));

    [Fact]
    public void Defaults_are_empty_lists_with_action_done_and_dry_run_on()
    {
        var settings = new AppsScriptSettings();

        settings.Rules.ShouldBeEmpty();
        settings.KeepInInboxLabels.ShouldBeEmpty();
        settings.ActionDoneArchive.ShouldBeTrue();
        settings.DryRun.ShouldBeTrue();
    }

    [Fact]
    public void Valid_block_passes()
    {
        Validate(new AppsScriptSettings
        {
            Rules = [new("Synthetic/News", 1), new("Synthetic Receipts", 3650)],
            KeepInInboxLabels = ["Synthetic/Keep"],
        }).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("INBOX")]
    [InlineData("A//B")]
    [InlineData("Bad\u0001Name")]
    public void Invalid_rule_label_is_a_field_error(string label)
    {
        Validate(new AppsScriptSettings { Rules = [new("Synthetic/Ok", 7), new(label, 7)] })
            .Keys.ShouldBe(["appsScript.rules[1].label"]);
    }

    [Fact]
    public void Too_long_rule_label_is_a_field_error()
    {
        Validate(new AppsScriptSettings { Rules = [new(new string('a', SettingsValidation.MaxLabelNameLength + 1), 7)] })
            .Keys.ShouldBe(["appsScript.rules[0].label"]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(3651)]
    public void Days_out_of_range_is_a_field_error(int days)
    {
        Validate(new AppsScriptSettings { Rules = [new("Synthetic/News", days)] }).Keys.ShouldBe(["appsScript.rules[0].days"]);
    }

    [Theory]
    [InlineData("Receipts & Bills")]
    [InlineData("example.com")]
    [InlineData("Synthetic 'single'")]
    [InlineData("-Synthetic")]
    [InlineData("Synthetic:News")]
    public void Unsearchable_labels_are_field_errors(string label)
    {
        Validate(new AppsScriptSettings { Rules = [new(label, 7)], KeepInInboxLabels = ["Synthetic/Keep", label] })
            .Keys.ShouldBe(["appsScript.rules[0].label", "appsScript.keepInInboxLabels[1]"], ignoreOrder: true);
    }

    [Fact]
    public void Searchable_labels_with_letters_digits_spaces_and_dashes_pass()
    {
        Validate(new AppsScriptSettings { Rules = [new("Synthetic/Ünïcode 2_x-y", 7)], KeepInInboxLabels = ["_Synthetic Keep"] }).ShouldBeEmpty();
    }

    [Fact]
    public void Rule_labels_that_search_the_same_are_duplicates()
    {
        Validate(new AppsScriptSettings { Rules = [new("Synthetic News", 7), new("Synthetic-News", 30)] })
            .Keys.ShouldBe(["appsScript.rules[1].label"]);
    }

    [Fact]
    public void Rule_labels_must_be_unique_ignoring_case()
    {
        var errors = Validate(new AppsScriptSettings { Rules = [new("Synthetic/News", 7), new(" synthetic/NEWS ", 30)] });

        errors.Keys.ShouldBe(["appsScript.rules[1].label"]);
        errors["appsScript.rules[1].label"].ShouldBe(["Each label may have one rule."]);
    }

    [Fact]
    public void More_than_the_maximum_rules_is_an_error()
    {
        var rules = Enumerable.Range(0, SettingsValidation.MaxAppsScriptRules + 1).Select(i => new ArchiveRule($"Synthetic/L{i}", 7)).ToList();

        Validate(new AppsScriptSettings { Rules = rules }).Keys.ShouldBe(["appsScript.rules"]);
        Validate(new AppsScriptSettings { Rules = rules[..SettingsValidation.MaxAppsScriptRules] }).ShouldBeEmpty();
    }

    [Fact]
    public void More_than_the_maximum_keep_in_inbox_labels_is_an_error()
    {
        var labels = Enumerable.Range(0, SettingsValidation.MaxKeepInInboxLabels + 1).Select(i => $"Synthetic/K{i}").ToList();

        Validate(new AppsScriptSettings { KeepInInboxLabels = labels }).Keys.ShouldBe(["appsScript.keepInInboxLabels"]);
        Validate(new AppsScriptSettings { KeepInInboxLabels = labels[..SettingsValidation.MaxKeepInInboxLabels] }).ShouldBeEmpty();
    }

    [Fact]
    public void Invalid_keep_in_inbox_label_is_a_field_error()
    {
        Validate(new AppsScriptSettings { KeepInInboxLabels = ["Synthetic/Keep", "SPAM"] }).Keys.ShouldBe(["appsScript.keepInInboxLabels[1]"]);
    }

    [Fact]
    public void Null_entries_from_the_json_body_are_field_errors()
    {
        var errors = Validate(new AppsScriptSettings { Rules = [null!, new(null!, 7)], KeepInInboxLabels = [null!] });

        errors.Keys.ShouldBe(["appsScript.rules[0]", "appsScript.rules[1].label", "appsScript.keepInInboxLabels[0]"], ignoreOrder: true);
    }

    [Fact]
    public void Normalise_trims_labels_and_turns_missing_lists_into_empty_ones()
    {
        var normalised = SettingsValidation.NormaliseAppsScript(new AppsScriptSettings { Rules = [new(" Synthetic/News ", 7)], KeepInInboxLabels = null! });

        normalised.Rules.ShouldBe([new ArchiveRule("Synthetic/News", 7)]);
        normalised.KeepInInboxLabels.ShouldBeEmpty();
    }

    [Fact]
    public void Generates_the_block_for_default_settings()
    {
        AppsScriptConfigGenerator.Generate(new AppSettings()).ShouldBe("""
            const CONFIG = {
              scriptVersion: 1,
              labelRules: [],
              actionLabel: "Action/ToDo",
              actionDoneArchive: true,
              keepInInboxLabels: [],
              pageSize: 100,
              maxRuntimeSeconds: 280,
              dryRun: true,
            };

            """.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Generates_the_block_for_populated_settings_with_the_current_action_label()
    {
        var settings = new AppSettings
        {
            ActionLabelName = "Synthetic/Act",
            AppsScript = new AppsScriptSettings
            {
                Rules = [new("Synthetic/News", 30), new("Synthetic Café", 7)],
                ActionDoneArchive = false,
                KeepInInboxLabels = ["Synthetic/Keep", "Synthetic/Pinned"],
                DryRun = false,
            },
        };

        AppsScriptConfigGenerator.Generate(settings).ShouldBe("""
            const CONFIG = {
              scriptVersion: 1,
              labelRules: [
                { label: "Synthetic/News", days: 30 },
                { label: "Synthetic Café", days: 7 },
              ],
              actionLabel: "Synthetic/Act",
              actionDoneArchive: false,
              keepInInboxLabels: [
                "Synthetic/Keep",
                "Synthetic/Pinned",
              ],
              pageSize: 100,
              maxRuntimeSeconds: 280,
              dryRun: false,
            };

            """.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Quotes_and_backslashes_are_escaped()
    {
        // Rule and keep-in-inbox labels can't carry quotes (see Unsearchable_*), but the action label can.
        var settings = new AppSettings { ActionLabelName = """Synthetic "Act" \ slash""" };

        AppsScriptConfigGenerator.Generate(settings).ShouldContain("""  actionLabel: "Synthetic \"Act\" \\ slash",""");
    }
}
