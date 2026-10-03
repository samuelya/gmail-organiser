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

/// <summary>
/// The rule for images (vision without a model leaves them out) is in <see cref="AttachmentSettings.EnabledTypes"/>;
/// that case is logged once until it changes, not on every read.
/// </summary>
public sealed class AttachmentPolicy(ISettingsStore settings, ILogger<AttachmentPolicy> logger) : IAttachmentPolicy
{
    // Per process: the policy is read per batch or message, and the setting rarely changes.
    private static int visionWithoutModelLogged;

    public async Task<AttachmentPolicySnapshot> GetAsync(CancellationToken ct)
    {
        var s = await settings.GetAsync(ct);
        var attachments = s.Attachments;
        var imagesBlocked = attachments.Enabled
            && attachments.Types.Any(t => t is { Type: AttachmentType.Image, Enabled: true })
            && !attachments.ImagesReadable(s.VisionModel);
        if (Interlocked.Exchange(ref visionWithoutModelLogged, imagesBlocked ? 1 : 0) == 0 && imagesBlocked)
        {
            logger.LogInformation("Image attachments are skipped: the image mode is vision and no vision model is selected in Settings");
        }

        return new AttachmentPolicySnapshot(
            attachments.Enabled, attachments.EnabledTypes(s.VisionModel), attachments.ToLimits(s.VisionModel, s.OllamaBaseUrl));
    }
}
