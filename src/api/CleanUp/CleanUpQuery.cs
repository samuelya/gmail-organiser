using GmailOrganiser.Analysis;
using GmailOrganiser.Common;
using GmailOrganiser.Data;
using GmailOrganiser.Fetch;
using GmailOrganiser.Review;
using GmailOrganiser.Senders;
using GmailOrganiser.Settings;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.CleanUp;

/// <summary>Which delete-labelled messages a clean-up action covers: the ids, the sender's, or (both null) all.</summary>
public sealed record CleanUpSelection(string[]? MessageIds = null, string? SenderAddress = null);

/// <summary>
/// The Clean-up page (DESIGN §3.3): stored messages carrying the delete label, not deleted in Gmail and not in Trash,
/// grouped by sender. The delete label is looked up by name and never created; without it the list is empty.
/// </summary>
public sealed class CleanUpQuery(AppDbContext db, LabelCatalog catalog, ISettingsStore settingsStore)
{
    public const string TrashLabel = MailboxFetchJob.TrashLabelId;
    public const string InboxLabel = ActionPlanner.InboxLabel;

    /// <summary>The Gmail id of the configured delete label, or null when Gmail has no such label.</summary>
    /// <exception cref="Gmail.GmailNotConnectedException">The app is not connected to Gmail.</exception>
    public async Task<string?> DeleteLabelIdAsync(CancellationToken ct) =>
        (await catalog.FindByNameAsync((await settingsStore.GetAsync(ct)).DeleteLabelName, ct))?.Id;

    /// <summary>Stored messages carrying <paramref name="deleteLabelId"/>, not deleted in Gmail and not in Trash.</summary>
    public static IQueryable<MessageRow> Candidates(AppDbContext db, string deleteLabelId) =>
        db.Messages.Where(m => !m.DeletedInGmail && m.LabelIds.Contains(deleteLabelId) && !m.LabelIds.Contains(TrashLabel));

    /// <summary>The <see cref="Candidates"/> in <paramref name="selection"/>.</summary>
    public static IQueryable<MessageRow> Selected(AppDbContext db, string deleteLabelId, CleanUpSelection selection)
    {
        var query = Candidates(db, deleteLabelId);
        if (selection.MessageIds is { } ids)
        {
            query = query.Where(m => ids.Contains(m.Id));
        }

        if (selection.SenderAddress is { } sender)
        {
            query = query.Where(m => m.FromAddress == sender);
        }

        return query;
    }

    /// <summary>The allowlisted addresses among <paramref name="senders"/>.</summary>
    public static async Task<HashSet<string>> AllowlistedAsync(AppDbContext db, IEnumerable<string> senders, CancellationToken ct)
    {
        var addresses = senders.Distinct(StringComparer.Ordinal).ToArray();
        return (await db.Senders.Where(s => s.Allowlisted && addresses.Contains(s.Address)).Select(s => s.Address).ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <exception cref="Gmail.GmailNotConnectedException">The app is not connected to Gmail.</exception>
    public async Task<CleanupSummaryDto> SummaryAsync(CancellationToken ct)
    {
        if (await DeleteLabelIdAsync(ct) is not { } label)
        {
            return new CleanupSummaryDto(0, 0, 0);
        }

        var flagged = Flag(Candidates(db, label).AsNoTracking(), (await settingsStore.GetAsync(ct)).Protection);
        return new CleanupSummaryDto(
            await flagged.CountAsync(ct),
            await flagged.Select(f => f.FromAddress).Distinct().CountAsync(ct),
            await flagged.CountAsync(f => f.Protected, ct));
    }

    /// <summary>Senders by message count, most first; <paramref name="search"/> matches the address or display name.</summary>
    /// <exception cref="Gmail.GmailNotConnectedException">The app is not connected to Gmail.</exception>
    public async Task<PagedDto<CleanupSenderDto>> SendersAsync(string? search, int page, int pageSize, CancellationToken ct)
    {
        if (await DeleteLabelIdAsync(ct) is not { } label)
        {
            return new PagedDto<CleanupSenderDto>([], page, pageSize, 0);
        }

        var messages = Candidates(db, label).AsNoTracking();
        if (search is not null)
        {
            var pattern = $"%{SenderQuery.EscapeLike(search)}%";
            messages = messages.Where(m => EF.Functions.ILike(m.FromAddress, pattern, "\\")
                || db.Senders.Any(s => s.Address == m.FromAddress && s.DisplayName != null && EF.Functions.ILike(s.DisplayName, pattern, "\\")));
        }

        var groups = Flag(messages, (await settingsStore.GetAsync(ct)).Protection)
            .GroupBy(f => f.FromAddress)
            .Select(g => new
            {
                Address = g.Key,
                Count = g.Count(),
                ProtectedCount = g.Count(f => f.Protected),
                OldestAt = g.Min(f => f.InternalDate),
                NewestAt = g.Max(f => f.InternalDate),
            });
        var total = await groups.LongCountAsync(ct);
        var rows = await groups.OrderByDescending(g => g.Count).ThenBy(g => g.Address)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(ct);
        var addresses = rows.ConvertAll(r => r.Address);
        var senders = await db.Senders.AsNoTracking()
            .Where(s => addresses.Contains(s.Address))
            .ToDictionaryAsync(s => s.Address, StringComparer.Ordinal, ct);
        return new PagedDto<CleanupSenderDto>(
            rows.ConvertAll(r => new CleanupSenderDto(
                r.Address, senders.GetValueOrDefault(r.Address)?.DisplayName, r.Count, r.ProtectedCount,
                r.OldestAt, r.NewestAt, senders.GetValueOrDefault(r.Address)?.Allowlisted == true)),
            page, pageSize, total);
    }

    /// <summary>The sender's messages newest first, each with why Delete would skip it.</summary>
    /// <exception cref="Gmail.GmailNotConnectedException">The app is not connected to Gmail.</exception>
    public async Task<PagedDto<CleanupMessageDto>> MessagesAsync(string address, int page, int pageSize, CancellationToken ct)
    {
        if (await DeleteLabelIdAsync(ct) is not { } label)
        {
            return new PagedDto<CleanupMessageDto>([], page, pageSize, 0);
        }

        var messages = Candidates(db, label).AsNoTracking().Where(m => m.FromAddress == address);
        var total = await messages.LongCountAsync(ct);
        var rows = await messages.OrderByDescending(m => m.InternalDate).ThenBy(m => m.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(ct);
        var allowlisted = await db.Senders.AnyAsync(s => s.Address == address && s.Allowlisted, ct);
        var rules = (await settingsStore.GetAsync(ct)).Protection;
        return new PagedDto<CleanupMessageDto>(
            rows.ConvertAll(m => new CleanupMessageDto(
                m.Id, m.Subject, m.Snippet, m.InternalDate, m.SizeEstimate,
                m.LabelIds.Contains(InboxLabel, StringComparer.Ordinal), MessageProtection.Reason(m, allowlisted, rules))),
            page, pageSize, total);
    }

    /// <summary>
    /// Each message with whether it is protected, in SQL so counts need not load the rows. Mirrors
    /// <see cref="MessageProtection.Reason"/>: a rule added there must be added here.
    /// </summary>
    private IQueryable<Flagged> Flag(IQueryable<MessageRow> messages, ProtectionSettings rules)
    {
        var (attachments, starred, important) = (rules.Attachments, rules.Starred, rules.Important);
        return messages.Select(m => new Flagged
        {
            FromAddress = m.FromAddress,
            InternalDate = m.InternalDate,
            Protected = db.Senders.Any(s => s.Address == m.FromAddress && s.Allowlisted)
                || (attachments && m.HasAttachment)
                || (starred && m.LabelIds.Contains(MessageProtection.StarredLabel))
                || (important && m.LabelIds.Contains(MessageProtection.ImportantLabel)),
        });
    }

    private sealed class Flagged
    {
        public string FromAddress { get; init; } = "";
        public DateTimeOffset InternalDate { get; init; }
        public bool Protected { get; init; }
    }
}
