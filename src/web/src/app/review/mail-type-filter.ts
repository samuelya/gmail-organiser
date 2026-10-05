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
}
