using GmailOrganiser.Settings;

namespace GmailOrganiser.Analysis.Attachments;

/// <summary>The effective attachment settings for <see cref="AttachmentConversionService"/>, read once per batch.</summary>
public interface IAttachmentPolicy
{
    Task<AttachmentPolicySnapshot> GetAsync(CancellationToken ct);
}

/// <param name="Enabled">
/// The master switch. Off means the prompt gets no attachment block at all, not even the skipped list, and
/// <paramref name="EnabledTypes"/> is empty.
/// </param>
/// <param name="EnabledTypes">Types the converters may read; never <see cref="AttachmentType.Archive"/>.</param>
public sealed record AttachmentPolicySnapshot(bool Enabled, IReadOnlySet<AttachmentType> EnabledTypes, ConversionLimits Limits);

public sealed class AttachmentPolicy(ISettingsStore settings) : IAttachmentPolicy
{
    public async Task<AttachmentPolicySnapshot> GetAsync(CancellationToken ct)
    {
        var attachments = (await settings.GetAsync(ct)).Attachments;
        return new AttachmentPolicySnapshot(attachments.Enabled, attachments.EnabledTypes(), attachments.ToLimits());
    }
}
