import { inject, Injectable } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router } from '@angular/router';
import { map } from 'rxjs';
import { MAIL_TYPES } from './mail-type-chip.component';
import { isSingleStatus, NOT_REJECTED_TAB, ReviewTab } from './review.models';

/** The review page's query parameter: comma-separated snake_case mail types. */
export const MAIL_TYPE_PARAM = 'mailType';

/** `?mailType=` → the known types it names, in `MAIL_TYPES` order; anything else is dropped. */
export function parseMailTypes(param: string | null): string[] {
  const named = new Set((param ?? '').split(',').map((t) => t.trim()));
  return MAIL_TYPES.filter((t) => named.has(t));
}

/** The selection → `?mailType=`; null removes the parameter. */
export function mailTypesParam(types: readonly string[]): string | null {
  const known = parseMailTypes(types.join(','));
  return known.length ? known.join(',') : null;
}

/** The senders list a detail belongs to: its tab, "Re-analysed" and mail types. */
export function listedKey(status: string, reanalysed: boolean, types: readonly string[]): string {
  return `${status}|${reanalysed}|${types.join(',')}`;
}

/** The suggestions a tab lists, for its empty texts: "pending re-analysed suggestions", "suggestions other than rejected". */
export function suggestionsKind(status: ReviewTab, reanalysed: boolean): string {
  if (!isSingleStatus(status)) return `suggestions ${NOT_REJECTED_TAB.phrase}`;
  return `${status} ${reanalysed ? 're-analysed ' : ''}suggestions`;
}

/** The detail's empty state: the sender has no suggestions of this kind, or none of the selected mail types. */
export function emptyGroupsText(
  status: ReviewTab,
  reanalysed: boolean,
  types: readonly string[],
): string {
  const kind = suggestionsKind(status, reanalysed);
  return types.length
    ? `No ${kind} of the selected mail types for this sender.`
    : `No ${kind} for this sender.`;
}

/**
 * The Review page's mail-type filter, kept in the query string. The API filters the senders list and the sender
 * detail by it; a matching group comes back whole.
 */
@Injectable()
export class MailTypeFilter {
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  readonly selected = toSignal(
    this.route.queryParamMap.pipe(map((p) => parseMailTypes(p.get(MAIL_TYPE_PARAM)))),
    // Other query parameters don't count as a filter change.
    { initialValue: [] as string[], equal: (a, b) => a.join(',') === b.join(',') },
  );

  set(types: readonly string[]): void {
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { [MAIL_TYPE_PARAM]: mailTypesParam(types) },
      queryParamsHandling: 'merge',
      replaceUrl: true,
    });
  }

  emptyText(status: ReviewTab, reanalysed: boolean): string {
    return emptyGroupsText(status, reanalysed, this.selected());
  }
}
