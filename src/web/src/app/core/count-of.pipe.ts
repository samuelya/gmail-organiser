import { formatNumber } from '@angular/common';
import { inject, LOCALE_ID, Pipe, PipeTransform } from '@angular/core';

/** Whole percent of `done` in `total`, rounded down and capped at 100; `null` without a total. */
export function percentOf(done: number, total: number | null | undefined): number | null {
  if (total === null || total === undefined || total <= 0) return null;
  return Math.min(100, Math.floor((done / total) * 100));
}

/** `3,200 of 12,450 · 25%`, or only `3,200` when the total is unknown. */
export function countOf(done: number, total: number | null | undefined, locale: string): string {
  const count = formatNumber(done, locale);
  if (total === null || total === undefined) return count;
  const percent = percentOf(done, total);
  const of = `${count} of ${formatNumber(total, locale)}`;
  return percent === null ? of : `${of} · ${percent}%`;
}

/** Template form of {@link countOf}: `done | countOf: total`. */
@Pipe({ name: 'countOf' })
export class CountOfPipe implements PipeTransform {
  private readonly locale = inject(LOCALE_ID);

  transform(done: number, total: number | null | undefined): string {
    return countOf(done, total, this.locale);
  }
}
