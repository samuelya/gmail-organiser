import { computed, inject, Signal } from '@angular/core';
import { MatDialog } from '@angular/material/dialog';
import { filter, Observable, switchMap } from 'rxjs';
import { openConfirm } from '../core/confirm-dialog';
import { JobsService } from '../core/jobs.service';
import { ANALYSIS_LIMITS } from '../settings/settings.models';
import { ANALYSIS_RUN_JOB, AnalysisRunDto, CompareRunRequest } from './analysis.models';
import { AnalysisService } from './analysis.service';

/** The API's `SettingsValidation.MaxAnalysisDefaultCount`: most emails one re-analysis takes. */
export const MAX_COMPARE = ANALYSIS_LIMITS.analysisDefaultCount.max;
/** Tooltip of every "Re-analyse" button while a run is queued or running. */
export const RUN_ACTIVE_TOOLTIP = 'An analysis is running';

/** True while any analysis run (normal or re-analysis) is queued, running or paused, from the jobs hub. */
export function injectAnalysisRunActive(): Signal<boolean> {
  const jobs = inject(JobsService);
  return computed(() => jobs.activeJobs().some((j) => j.type === ANALYSIS_RUN_JOB));
}

/** Tooltip of a run's "Re-analyse" when it covers more emails than one re-analysis takes. */
export const RUN_TOO_LARGE_TOOLTIP = `At most ${MAX_COMPARE.toLocaleString()} emails per re-analysis`;

/**
 * The confirm text: what goes to the LLM, with what, and that nothing changes until the user picks.
 * `upTo`: the count is a ceiling (a run's emails; resolved suggestions are left out by the API).
 */
export function compareConfirmMessage(count: number, upTo = false): string {
  const verb = count === 1 ? 'email goes' : 'emails go';
  const emails = `${upTo ? 'Up to ' : ''}${count.toLocaleString()} ${verb}`;
  return (
    `${emails} to the LLM with the current prompt and settings. ` +
    `The memory shortcut is skipped (memory hints still apply). ` +
    `Current suggestions stay as they are until you pick "Use new" or "Keep current".`
  );
}

/** Asks before a re-analysis of `count` (or `upTo` count) emails; emits `true` only when confirmed. */
export function openCompareConfirm(
  dialog: MatDialog,
  count: number,
  upTo = false,
): Observable<boolean> {
  const noun = count === 1 ? 'email' : 'emails';
  return openConfirm(dialog, {
    title: `Re-analyse ${upTo ? 'up to ' : ''}${count.toLocaleString()} ${noun}?`,
    message: compareConfirmMessage(count, upTo),
    confirm: 'Re-analyse',
  });
}

/**
 * Confirms, then starts the re-analysis; emits the queued run, nothing when cancelled. Errors (a 409 among
 * them) are shown by the error interceptor and rethrown.
 */
export function confirmCompareRun(
  dialog: MatDialog,
  analysis: AnalysisService,
  request: CompareRunRequest,
  count: number,
  upTo = false,
): Observable<AnalysisRunDto> {
  return openCompareConfirm(dialog, count, upTo).pipe(
    filter((confirmed) => confirmed),
    switchMap(() => analysis.startCompareRun(request)),
  );
}
