using GmailOrganiser.Gmail;

namespace GmailOrganiser.Review;

/// <summary>Resolves label paths to Gmail label ids through <see cref="LabelCatalog"/>, creating missing ones parent-first.</summary>
public sealed class LabelResolver(LabelCatalog catalog, IGmailClient gmail)
{
    /// <summary>Gmail's cap on labels per mailbox.</summary>
    public const int MaxLabels = 10_000;

    /// <summary>
    /// A path Gmail accepts as a user label: <see cref="LabelPath.IsValid"/> and no reserved name at any level, so
    /// <c>INBOX/Sub</c> is refused like <c>INBOX</c>.
    /// </summary>
    public static bool IsValid(string path) =>
        LabelPath.IsValid(path) && !Prefixes(path).Any(LabelPath.IsReserved);

    /// <summary>
    /// The label id of every path (keys compared case-insensitively). Missing labels are created parent-first
    /// (<c>A</c>, then <c>A/B</c>) under the existing parent's spelling; the catalog is invalidated after a create.
    /// <paramref name="created"/> runs after each create, before the next.
    /// </summary>
    /// <exception cref="ArgumentException">A path is not <see cref="IsValid"/>.</exception>
    /// <exception cref="LabelLimitException">Creating the labels would pass <see cref="MaxLabels"/>.</exception>
    /// <exception cref="GmailNotConnectedException">The app is not connected to Gmail.</exception>
    /// <exception cref="GmailRateLimitedException">Gmail kept rate-limiting after the last retry.</exception>
    public async Task<IReadOnlyDictionary<string, string>> EnsureAsync(
        IEnumerable<string> paths, Func<GmailLabel, CancellationToken, Task>? created, CancellationToken ct)
    {
        var wanted = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (wanted.FirstOrDefault(p => !IsValid(p)) is { } invalid)
        {
            throw new ArgumentException($"'{invalid}' is not a valid label path.", nameof(paths));
        }

        var labels = new List<GmailLabel>(await catalog.GetAsync(ct));
        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var any = false;
        try
        {
            foreach (var path in wanted)
            {
                GmailLabel? label = null;
                foreach (var segment in path.Split('/'))
                {
                    var name = label is null ? segment : $"{label.Name}/{segment}";
                    label = GmailLabel.FindByName(labels, name);
                    if (label is null)
                    {
                        if (labels.Count >= MaxLabels)
                        {
                            throw new LabelLimitException($"Gmail allows at most {MaxLabels} labels; '{path}' was not created.");
                        }

                        label = await gmail.CreateLabelAsync(name, ct);
                        labels.Add(label);
                        any = true;
                        if (created is not null)
                        {
                            await created(label, ct);
                        }
                    }
                }

                resolved[path] = label!.Id;
            }
        }
        finally
        {
            if (any)
            {
                catalog.Invalidate();
            }
        }

        return resolved;
    }

    private static IEnumerable<string> Prefixes(string path)
    {
        for (var i = path.IndexOf('/'); i >= 0; i = path.IndexOf('/', i + 1))
        {
            yield return path[..i];
        }

        yield return path;
    }
}

/// <summary>Creating a label would pass <see cref="LabelResolver.MaxLabels"/>.</summary>
public sealed class LabelLimitException(string message) : InvalidOperationException(message);
