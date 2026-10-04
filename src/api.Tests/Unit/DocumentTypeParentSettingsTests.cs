using GmailOrganiser.Settings;

namespace GmailOrganiser.Tests.Unit;

/// <summary>Validation of the document-type parent label (#238).</summary>
public sealed class DocumentTypeParentSettingsTests
{
    private static readonly AppSettings Current = new() { ActionLabelName = "Synthetic/Action", DeleteLabelName = "Synthetic Delete" };

    public static TheoryData<string> InvalidParents => new()
    {
        "INBOX",
        "spam",
        "Trash/Sub",
        new string('a', SettingsValidation.MaxDocumentTypeParentLength + 1),
        "A/B/C/D/E",
        "A//B",
        "/Leading",
        "Bad\u0001Name",
        new string('a', 101),
    };

    [Theory]
    [MemberData(nameof(InvalidParents))]
    public void Invalid_parents_are_field_errors(string parent)
    {
        Validate(parent).Keys.ShouldBe(["documentTypeParent"]);
    }

    [Theory]
    [InlineData("Synthetic Documents")]
    [InlineData("  Synthetic/Docs  ")]
    [InlineData("A/B/C/D")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Synthetic/Actionable")]
    public void Valid_or_blank_parents_pass(string parent)
    {
        Validate(parent).ShouldBeEmpty();
    }

    [Fact]
    public void A_parent_of_the_maximum_length_passes()
    {
        var parent = new string('a', 100) + "/" + new string('b', SettingsValidation.MaxDocumentTypeParentLength - 101);

        Validate(parent).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("synthetic delete")]
    [InlineData("Synthetic Delete/Docs")]
    [InlineData("SYNTHETIC/ACTION")]
    [InlineData("Synthetic")]
    [InlineData("synthetic/action/Docs")]
    public void A_parent_equal_to_above_or_under_the_action_or_delete_label_is_refused(string parent)
    {
        var errors = Validate(parent);

        errors.Keys.ShouldBe(["documentTypeParent"]);
        errors["documentTypeParent"].ShouldBe([SettingsValidation.DocumentTypeParentClashMessage]);
    }

    [Fact]
    public void The_parent_is_checked_against_the_label_names_sent_with_it()
    {
        var request = new UpdateSettingsRequest(null, null, null, null, ActionLabelName: "Synthetic Docs/Todo", DocumentTypeParent: "Synthetic Docs");

        SettingsValidation.Validate(request, Current).Keys.ShouldBe(["documentTypeParent"]);
    }

    [Fact]
    public void A_label_name_sent_alone_is_checked_against_the_stored_parent()
    {
        var current = Current with { DocumentTypeParent = "Synthetic Docs" };

        SettingsValidation.Validate(new UpdateSettingsRequest(null, null, null, null, DeleteLabelName: "synthetic docs/bin"), current)
            .Keys.ShouldBe(["documentTypeParent"]);
        SettingsValidation.Validate(new UpdateSettingsRequest(null, null, null, null, DeleteLabelName: "Synthetic Bin"), current)
            .ShouldBeEmpty();
    }

    [Theory]
    [InlineData("  Synthetic Docs ", "Synthetic Docs")]
    [InlineData("", null)]
    [InlineData("  ", null)]
    public void Normalise_trims_and_maps_blank_to_off(string value, string? expected)
    {
        SettingsValidation.NormaliseDocumentTypeParent(value).ShouldBe(expected);
    }

    private static Dictionary<string, string[]> Validate(string parent) =>
        SettingsValidation.Validate(new UpdateSettingsRequest(null, null, null, null, DocumentTypeParent: parent), Current);
}
