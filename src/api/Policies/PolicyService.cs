using GmailOrganiser.Analysis;
using GmailOrganiser.Data;
using GmailOrganiser.Fetch;
using GmailOrganiser.Jobs;
using GmailOrganiser.Review;
using GmailOrganiser.Settings;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Policies;

/// <summary>The outcome of a policy change; the endpoint maps it to 200, 400, 404, 409 or 422.</summary>
public enum PolicyChange
{
    Done,
    Invalid,
    NotFound,
    Conflict,

    /// <summary>The policy cannot apply: an outcome it can produce has no topic label (see <see cref="PolicyService.Unappliable"/>).</summary>
    Unappliable,
}

/// <summary>
/// The owner's decisions on sender policies (DESIGN §6.3): edit, approve, reject, per-rule decisions and delete. Changes
/// only the database; an approved policy reaches Gmail through the <see cref="PolicyApplyJob"/> (#359).
/// </summary>
public sealed class PolicyService(AppDbContext db, ISettingsStore settings, IJobService jobs, TimeProvider time)
{
    public const int MaxRuleNameLength = 200;

    /// <summary>
    /// Replaces the policy's fields and rule list (positions = array order). Kept rules keep their status, source and
    /// reason; new rules are the user's and approved. Only proposed and approved policies are editable.
    /// </summary>
    /// <returns><see cref="PolicyChange.Invalid"/> with the field errors; <c>Reapply</c> when the policy was approved.</returns>
    public async Task<(PolicyChange Change, Dictionary<string, string[]> Errors, bool Reapply)> EditAsync(
        Guid id, EditPolicyRequest request, CancellationToken ct)
    {
        var policy = await db.SenderPolicies.Include(p => p.Rules).FirstOrDefaultAsync(p => p.Id == id, ct);
        if (policy is null)
        {
            return (PolicyChange.NotFound, [], false);
        }

        if (policy.Status == PolicyStatus.Rejected)
        {
            return (PolicyChange.Conflict, [], false);
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var parent = (await settings.GetAsync(ct)).DocumentTypeParent;
        var topic = Label(request.TopicLabel, "topicLabel", errors);
        var documentType = DocumentType(request.DocumentTypeLabel, parent, topic, "documentTypeLabel", errors);
        var mailType = ParseOptional<MailType>(request.MailType, "mailType", errors);
        var action = ParseRequired<PolicyAction>(request.Action, "action", errors);
        Retention(request.RetentionDays, "retentionDays", errors);
        if (request.IsMixed is null)
        {
            errors["isMixed"] = ["Required: true or false."];
        }

        var existing = policy.Rules.ToDictionary(r => r.Id);
        var kept = new HashSet<Guid>();
        var now = time.GetUtcNow();
        var rules = new List<SenderPolicyRuleRow>();
        if (request.Rules is null)
        {
            errors["rules"] = ["Required: an array (may be empty)."];
        }

        var requested = request.Rules ?? [];
        for (var i = 0; i < requested.Length; i++)
        {
            var field = $"rules[{i}]";
            if (requested[i] is not { } r)
            {
                errors[field] = ["Required."];
                continue;
            }

            SenderPolicyRuleRow? rule = null;
            if (r.Id is { } ruleId && (!existing.TryGetValue(ruleId, out rule) || !kept.Add(ruleId)))
            {
                errors[$"{field}.id"] = ["Not a rule of this policy, or listed twice."];
                continue;
            }

            rule ??= new SenderPolicyRuleRow
            {
                Id = Guid.NewGuid(),
                PolicyId = policy.Id,
                Status = PolicyStatus.Approved,
                Source = PolicyRuleSource.User,
                CreatedAt = now,
            };
            var name = r.Name?.Trim() ?? "";
            if (name.Length is 0 or > MaxRuleNameLength)
            {
                errors[$"{field}.name"] = [$"Required, at most {MaxRuleNameLength} characters."];
            }

            var ruleTopic = Label(r.TopicLabel, $"{field}.topicLabel", errors);
            rule.Position = i;
            rule.Name = name;
            rule.Match = Match(r.Match, $"{field}.match", errors);
            rule.TopicLabel = ruleTopic ?? "";
            rule.DocumentTypeLabel = DocumentType(r.DocumentTypeLabel, parent, ruleTopic, $"{field}.documentTypeLabel", errors);
            rule.MailType = ParseOptional<MailType>(r.MailType, $"{field}.mailType", errors);
            rule.RetentionDays = Retention(r.RetentionDays, $"{field}.retentionDays", errors);
            rule.Action = ParseRequired<PolicyAction>(r.Action, $"{field}.action", errors) ?? default;
            rules.Add(rule);
        }

        policy.TopicLabel = topic;
        policy.DocumentTypeLabel = documentType;
        policy.MailType = mailType;
        policy.RetentionDays = request.RetentionDays;
        policy.Action = action ?? policy.Action;
        policy.IsMixed = request.IsMixed ?? policy.IsMixed;
        if (errors.Count == 0)
        {
            // The shared rules (mixed default, required topic, label limits, rule count) on the edited policy.
            var candidate = new SenderPolicyRow
            {
                IsMixed = policy.IsMixed,
                TopicLabel = policy.TopicLabel,
                DocumentTypeLabel = policy.DocumentTypeLabel,
                Action = policy.Action,
                Rules = rules,
            };
            errors = PolicyValidation.Validate(candidate);
        }

        if (errors.Count > 0)
        {
            // Nothing is saved: the tracked changes die with the request's context.
            return (PolicyChange.Invalid, errors, false);
        }

        db.SenderPolicyRules.RemoveRange(policy.Rules.Where(r => !kept.Contains(r.Id)));
        db.SenderPolicyRules.AddRange(rules.Where(r => !existing.ContainsKey(r.Id)));
        policy.Edited = true;
        await db.SaveChangesAsync(ct);
        return (PolicyChange.Done, [], policy.Status == PolicyStatus.Approved);
    }

    /// <summary>
    /// A proposed policy becomes approved with every proposed rule (rejected rules stay rejected), and its
    /// <see cref="PolicyApplyJob"/> is queued in the same transaction; nothing changes when the approved policy could not apply.
    /// </summary>
    public async Task<(PolicyChange Change, Guid? JobId, string? Reason)> ApproveAsync(Guid id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var change = await DecideAsync(id, PolicyStatus.Approved, ct);
        if (change != PolicyChange.Done)
        {
            return (change, null, null);
        }

        var policy = await db.SenderPolicies.Include(p => p.Rules).FirstAsync(p => p.Id == id, ct);
        if (Unappliable(policy) is { } reason)
        {
            // Rolled back with the transaction: the policy stays proposed.
            return (PolicyChange.Unappliable, null, reason);
        }

        var (job, _) = await EnqueueApplyAsync(id, ct);
        await tx.CommitAsync(ct);
        return (change, job.Id, null);
    }

    /// <summary>
    /// Re-runs the <see cref="PolicyApplyJob"/> of an approved policy (after an edit); a conflict when the policy is
    /// not approved or its job is already queued or running.
    /// </summary>
    public async Task<(PolicyChange Change, Guid? JobId, string? Reason)> ApplyAsync(Guid id, CancellationToken ct)
    {
        var policy = await db.SenderPolicies.AsNoTracking().Include(p => p.Rules).FirstOrDefaultAsync(p => p.Id == id, ct);
        if (policy is null)
        {
            return (PolicyChange.NotFound, null, null);
        }

        if (policy.Status != PolicyStatus.Approved)
        {
            return (PolicyChange.Conflict, null, null);
        }

        if (Unappliable(policy) is { } reason)
        {
            return (PolicyChange.Unappliable, null, reason);
        }

        var (job, created) = await EnqueueApplyAsync(id, ct);
        return created ? (PolicyChange.Done, job.Id, null) : (PolicyChange.Conflict, null, null);
    }

    /// <summary>
    /// Why an approved policy cannot apply, or null when it can: every approved rule, and the default unless the sender
    /// is mixed (only a non-mixed sender's default or transactional guard uses it), needs a topic label Gmail accepts,
    /// because the apply labels every message it touches.
    /// </summary>
    public static string? Unappliable(SenderPolicyRow policy)
    {
        var rule = policy.Rules.Where(r => r.Status == PolicyStatus.Approved).OrderBy(r => r.Position)
            .FirstOrDefault(r => !LabelResolver.IsValid(r.TopicLabel));
        if (rule is not null)
        {
            return $"Rule '{rule.Name}' has no usable topic label; set one before applying.";
        }

        return !policy.IsMixed && !LabelResolver.IsValid(policy.TopicLabel ?? "")
            ? "The policy's default has no usable topic label; set one before applying."
            : null;
    }

    /// <summary>A proposed policy becomes rejected; its rules are left as they are.</summary>
    public Task<PolicyChange> RejectAsync(Guid id, CancellationToken ct) => DecideAsync(id, PolicyStatus.Rejected, ct);

    /// <summary>
    /// Approves or rejects one rule of a proposed or approved policy; a rule already in <paramref name="status"/> or of a
    /// rejected policy is a conflict.
    /// </summary>
    public async Task<PolicyChange> DecideRuleAsync(Guid id, Guid ruleId, PolicyStatus status, CancellationToken ct)
    {
        var policy = await db.SenderPolicies.Include(p => p.Rules).FirstOrDefaultAsync(p => p.Id == id, ct);
        var rule = policy?.Rules.Find(r => r.Id == ruleId);
        if (policy is null || rule is null)
        {
            return PolicyChange.NotFound;
        }

        if (policy.Status == PolicyStatus.Rejected || rule.Status == status)
        {
            return PolicyChange.Conflict;
        }

        rule.Status = status;
        await db.SaveChangesAsync(ct);
        return PolicyChange.Done;
    }

    /// <summary>Deletes a rejected policy and its rules, so the sender can be proposed again.</summary>
    public async Task<PolicyChange> DeleteAsync(Guid id, CancellationToken ct)
    {
        var policy = await db.SenderPolicies.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (policy is null)
        {
            return PolicyChange.NotFound;
        }

        if (policy.Status != PolicyStatus.Rejected)
        {
            return PolicyChange.Conflict;
        }

        db.SenderPolicies.Remove(policy);
        await db.SaveChangesAsync(ct);
        return PolicyChange.Done;
    }

    private async Task<PolicyChange> DecideAsync(Guid id, PolicyStatus status, CancellationToken ct)
    {
        var policy = await db.SenderPolicies.Include(p => p.Rules).FirstOrDefaultAsync(p => p.Id == id, ct);
        if (policy is null)
        {
            return PolicyChange.NotFound;
        }

        if (policy.Status != PolicyStatus.Proposed)
        {
            return PolicyChange.Conflict;
        }

        policy.Status = status;
        policy.DecidedAt = time.GetUtcNow();
        if (status == PolicyStatus.Approved)
        {
            policy.Rules.Where(r => r.Status == PolicyStatus.Proposed).ToList().ForEach(r => r.Status = PolicyStatus.Approved);
        }

        await db.SaveChangesAsync(ct);
        return PolicyChange.Done;
    }

    private Task<(JobDto Job, bool Created)> EnqueueApplyAsync(Guid id, CancellationToken ct) =>
        jobs.EnqueueAsync(PolicyApplyJob.JobType, PolicyApplyJob.Queue, new PolicyApplyCursor(id), ct, id.ToString());

    /// <summary>A trimmed user label path, or null when blank; unknown labels are fine (created on apply).</summary>
    private static string? Label(string? value, string field, Dictionary<string, string[]> errors)
    {
        var label = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (label is not null && !LabelResolver.IsValid(label))
        {
            errors[field] = ["Not a label path Gmail accepts: up to five '/'-separated parts, not a Gmail system label."];
        }

        return label;
    }

    /// <summary>Null or blank for none; otherwise checked like the review edit (<see cref="DocumentTypeEdit"/>).</summary>
    private static string? DocumentType(string? value, string? parent, string? topic, string field, Dictionary<string, string[]> errors)
    {
        var own = new Dictionary<string, string[]>();
        var change = DocumentTypeEdit.Validate(string.IsNullOrWhiteSpace(value) ? null : value, parent, topic, own);
        if (own.TryGetValue(DocumentTypeEdit.Field, out var messages))
        {
            errors[field] = messages;
        }

        return change.Label;
    }

    private static int? Retention(int? days, string field, Dictionary<string, string[]> errors)
    {
        if (days is <= 0)
        {
            errors[field] = ["Must be a positive number of days, or null for the mail type's default."];
        }

        return days;
    }

    private static RuleMatch Match(RuleMatchDto? dto, string field, Dictionary<string, string[]> errors)
    {
        if (dto is null)
        {
            return new RuleMatch();
        }

        return new RuleMatch
        {
            ListIdPresent = dto.ListIdPresent,
            ListUnsubscribePresent = dto.ListUnsubscribePresent,
            FromAddress = Trimmed(dto.FromAddress),
            FromSubdomain = Trimmed(dto.FromSubdomain),
            Category = ParseOptional<MessageCategory>(dto.Category, $"{field}.category", errors),
            SubjectTemplate = Trimmed(dto.SubjectTemplate),
            SubjectContains = Trimmed(dto.SubjectContains),
        };
    }

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static TEnum? ParseOptional<TEnum>(string? value, string field, Dictionary<string, string[]> errors)
        where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (SnakeCaseEnumConverter<TEnum>.TryFromDb(value, out var parsed))
        {
            return parsed;
        }

        errors[field] = [$"Must be one of {SnakeCaseEnumConverter<TEnum>.NamesList}."];
        return null;
    }

    private static TEnum? ParseRequired<TEnum>(string? value, string field, Dictionary<string, string[]> errors)
        where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors[field] = [$"Required: one of {SnakeCaseEnumConverter<TEnum>.NamesList}."];
            return null;
        }

        return ParseOptional<TEnum>(value, field, errors);
    }
}
