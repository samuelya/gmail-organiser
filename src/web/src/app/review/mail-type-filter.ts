import { inject, Injectable } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router } from '@angular/router';
import { map } from 'rxjs';
import { MAIL_TYPES } from './mail-type-chip.component';
import { ReviewGroupDto } from './review.models';

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

/** All groups when nothing is selected; else those with a listed member of a selected type. */
export function filterByMailType(
  groups: readonly ReviewGroupDto[],
  types: readonly string[],
): readonly ReviewGroupDto[] {
  if (!types.length) return groups;
  const selected = new Set(types);
  return groups.filter((g) => g.members.some((m) => !!m.mailType && selected.has(m.mailType)));
}

/** The ticked ids the filter still shows; null when it hides none of them. */
export function keptSelection(
  selection: ReadonlySet<string>,
  groups: readonly ReviewGroupDto[],
  types: readonly string[],
): ReadonlySet<string> | null {
  if (selection.size === 0) return null;
  const shown = new Set(filterByMailType(groups, types).flatMap((g) => g.members.map((m) => m.id)));
  const kept = [...selection].filter((id) => shown.has(id));
  return kept.length === selection.size ? null : new Set(kept);
}

/**
 * The detail's empty state. The filter narrows only the listed page, so with other pages it says "on this
 * page" rather than claiming the sender has none.
 */
export function emptyGroupsText(
  status: string,
  reanalysed: boolean,
  types: readonly string[],
  paged: boolean,
): string {
  const kind = `${status} ${reanalysed ? 're-analysed ' : ''}suggestions`;
  if (!types.length) return `No ${kind} for this sender.`;
  return `No ${kind} of the selected mail types ${paged ? 'on this page' : 'for this sender'}.`;
}

/**
 * The Review page's mail-type filter, kept in the query string. The API has no mail-type filter, so it narrows
 * the selected sender's listed groups; the senders list and its counts are not filtered.
 */
@Injectable()
export class MailTypeFilter {
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  readonly selected = toSignal(
    this.route.queryParamMap.pipe(map((p) => parseMailTypes(p.get(MAIL_TYPE_PARAM)))),
    { initialValue: [] as string[] },
  );

  set(types: readonly string[]): void {
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { [MAIL_TYPE_PARAM]: mailTypesParam(types) },
      queryParamsHandling: 'merge',
      replaceUrl: true,
    });
  }

  shown(groups: readonly ReviewGroupDto[]): readonly ReviewGroupDto[] {
    return filterByMailType(groups, this.selected());
  }

  kept(
    selection: ReadonlySet<string>,
    groups: readonly ReviewGroupDto[],
  ): ReadonlySet<string> | null {
    return keptSelection(selection, groups, this.selected());
  }

  emptyText(status: string, reanalysed: boolean, paged: boolean): string {
    return emptyGroupsText(status, reanalysed, this.selected(), paged);
  }
}
