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

/** The confirm text: what goes to the LLM, with what, and that nothing changes until the user picks. */
export function compareConfirmMessage(count: number): string {
  const emails = `${count.toLocaleString()} ${count === 1 ? 'email goes' : 'emails go'}`;
  return (
    `${emails} to the LLM with the current prompt and settings. ` +
    `The memory shortcut is skipped (memory hints still apply). ` +
    `Current suggestions stay as they are until you pick "Use new" or "Keep current".`
  );
}

/** Asks before a re-analysis of `count` emails; emits `true` only when confirmed. */
export function openCompareConfirm(dialog: MatDialog, count: number): Observable<boolean> {
  return openConfirm(dialog, {
    title: `Re-analyse ${count.toLocaleString()} ${count === 1 ? 'email' : 'emails'}?`,
    message: compareConfirmMessage(count),
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
): Observable<AnalysisRunDto> {
  return openCompareConfirm(dialog, count).pipe(
    filter((confirmed) => confirmed),
    switchMap(() => analysis.startCompareRun(request)),
  );
}
