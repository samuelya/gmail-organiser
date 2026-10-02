namespace GmailOrganiser.Common;

/// <summary>One page of a server-side paged list; <see cref="Total"/> counts every matching item.</summary>
public sealed record PagedDto<T>(IReadOnlyList<T> Items, int Page, int PageSize, long Total);
