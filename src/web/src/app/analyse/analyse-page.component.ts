import { HttpErrorResponse } from '@angular/common/http';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  effect,
  inject,
  signal,
  untracked,
} from '@angular/core';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatAutocompleteModule } from '@angular/material/autocomplete';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatCardModule } from '@angular/material/card';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { ActivatedRoute, RouterLink } from '@angular/router';
import {
  catchError,
  debounceTime,
  distinctUntilChanged,
  filter,
  finalize,
  forkJoin,
  map,
  merge,
  of,
  startWith,
  Subject,
  switchMap,
  tap,
} from 'rxjs';
import { openConfirm } from '../core/confirm-dialog';
import { JobsService } from '../core/jobs.service';
import { PageHeader } from '../layout/page-header';
import { cleanSearch } from '../senders/senders.models';
import { SendersService } from '../senders/senders.service';
import { SettingsService } from '../settings/settings.service';
import {
  ACTIVE_RUNS_LIMIT,
  AnalysisRunDto,
  AnalysisScope,
  AnalysisSelection,
  analysisJobsKey,
  COUNT_PRESETS,
  DEFAULT_TOP_SENDERS,
  FINISHED_RUNS_SHOWN,
  GroupingPreviewDto,
  MAX_SENDER_LENGTH,
  MAX_TOP_SENDERS,
  parseAnalyseParams,
  queueOrder,
  SCOPE_OPTIONS,
  TOP_SENDERS,
} from './analysis.models';
import { AnalysisService } from './analysis.service';
import { confirmCompareRun, injectAnalysisRunActive, MAX_COMPARE } from './compare-run';
import { GroupingPreview } from './grouping-preview.component';
import { RunList } from './run-list.component';

export const PREVIEW_DEBOUNCE_MS = 400;
export const SENDER_SEARCH_DEBOUNCE_MS = 300;
const SENDER_OPTIONS = 10;
const MAX_COUNT = 1000;

type CountPreset = number | 'custom';
/** What the count counts: emails, or senders for the top senders scope. */
type CountUnit = 'emails' | 'senders';

interface FormValue {
  scope: AnalysisScope;
  sender: string;
  preset: CountPreset | null;
  customCount: number | null;
}

/** The selection to preview and start, or `null` while it is incomplete or invalid. */
export function selectionOf(v: FormValue): AnalysisSelection | null {
  const count = countOf(v);
  if (count === null || !Number.isInteger(count) || count < 1 || count > maxCountOf(v.scope)) {
    return null;
  }
  if (v.scope !== 'sender' && v.scope !== TOP_SENDERS) return { scope: v.scope, count };
  const sender = v.sender.trim();
  if (sender.length > MAX_SENDER_LENGTH) return null;
  // The top senders scope takes an optional sender; the sender scope needs one.
  if (!sender) return v.scope === TOP_SENDERS ? { scope: v.scope, count } : null;
  return { scope: v.scope, senderAddress: sender, count };
}

function countOf(v: Pick<FormValue, 'preset' | 'customCount'>): number | null {
  return v.preset === 'custom' ? v.customCount : v.preset;
}

function unitOf(scope: AnalysisScope): CountUnit {
  return scope === TOP_SENDERS ? 'senders' : 'emails';
}

function maxCountOf(scope: AnalysisScope): number {
  return unitOf(scope) === 'senders' ? MAX_TOP_SENDERS : MAX_COUNT;
}

/** `/analyse`: choose a scope and count with a live grouping preview, start a run, and follow the run queue. */
@Component({
  selector: 'app-analyse-page',
  imports: [
    GroupingPreview,
    MatAutocompleteModule,
    MatButtonModule,
    MatButtonToggleModule,
    MatCardModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    PageHeader,
    ReactiveFormsModule,
    RouterLink,
    RunList,
  ],
  templateUrl: './analyse-page.component.html',
  styles: `
    .card-title {
      font: var(--mat-sys-title-medium);
      margin: 0;
    }
    .label {
      font: var(--mat-sys-label-large);
    }
    .muted {
      color: var(--mat-sys-on-surface-variant);
    }
    .banner {
      background: var(--mat-sys-error-container);
      color: var(--mat-sys-on-error-container);
      border-radius: var(--mat-sys-corner-medium);
    }
    .banner a {
      color: inherit;
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AnalysePage {
  private readonly analysis = inject(AnalysisService);
  private readonly senders = inject(SendersService);
  private readonly settings = inject(SettingsService);
  private readonly route = inject(ActivatedRoute);
  private readonly dialog = inject(MatDialog);
  private readonly destroyRef = inject(DestroyRef);
  private readonly jobs = inject(JobsService);
  private readonly reloadRuns = new Subject<void>();
  /** Asks for a fresh preview of the same selection: a started or finished run changes what is left. */
  private readonly refreshPreview = new Subject<void>();
  /** The deep link set the count: the settings default must not overwrite it. */
  private countFromLink = false;
  /** The settings default for an email count, once loaded. */
  private emailDefault: number | null = null;
  /** The count each unit had when the scope last switched away from it. */
  private readonly savedCounts: Record<CountUnit, number | null> = {
    emails: null,
    senders: null,
  };
  private unit: CountUnit = 'emails';

  readonly presets = COUNT_PRESETS;
  readonly maxSender = MAX_SENDER_LENGTH;
  readonly form = new FormGroup({
    scope: new FormControl<AnalysisScope>('inbox', { nonNullable: true }),
    sender: new FormControl('', {
      nonNullable: true,
      validators: [Validators.maxLength(MAX_SENDER_LENGTH)],
    }),
    preset: new FormControl<CountPreset | null>(null),
    customCount: new FormControl<number | null>(null, [
      Validators.min(1),
      Validators.max(MAX_COUNT),
      Validators.pattern(/^\d+$/),
    ]),
  });

  private readonly formValue = toSignal(
    this.form.valueChanges.pipe(
      startWith(null),
      map(() => this.form.getRawValue()),
    ),
    { requireSync: true },
  );
  readonly selection = computed(() => selectionOf(this.formValue()));
  readonly isCustom = computed(() => this.formValue().preset === 'custom');
  readonly isTopSenders = computed(() => this.formValue().scope === TOP_SENDERS);
  /** The sender field: required for the sender scope, optional for top senders. */
  readonly showSender = computed(() => this.formValue().scope === 'sender' || this.isTopSenders());
  readonly maxCount = computed(() => maxCountOf(this.formValue().scope));
  readonly scopes = SCOPE_OPTIONS;
  readonly scopeHelp = computed(
    () => SCOPE_OPTIONS.find((o) => o.value === this.formValue().scope)?.help ?? null,
  );

  readonly preview = signal<GroupingPreviewDto | null>(null);
  readonly previewLoading = signal(false);
  readonly senderOptions = signal<string[]>([]);
  readonly active = signal<AnalysisRunDto[]>([]);
  readonly finished = signal<AnalysisRunDto[]>([]);
  readonly runsFailed = signal(false);
  /** The API returned as many active runs as asked for: the oldest may be missing. */
  readonly queueTruncated = signal(false);
  readonly starting = signal(false);
  /** The last start answered 409: no chat model is selected. */
  readonly noChatModel = signal(false);
  readonly cancelling = signal<ReadonlySet<string>>(new Set());
  /** Run ids whose resume request is in flight. */
  readonly resuming = signal<ReadonlySet<string>>(new Set());
  /** "Failed" filter chip on the finished runs: the API lists only failed runs. */
  readonly failedOnly = signal(false);
  private readonly hubRunActive = injectAnalysisRunActive();
  /** A run is queued or running (by the hub or the last list): no re-analysis can start. */
  readonly runActive = computed(() => this.hubRunActive() || this.active().length > 0);

  /** Run lists reload when an analysis job appears or changes status, or after a reconnect. */
  private readonly runsKey = computed(
    () => `${analysisJobsKey(this.jobs.jobs())}|${this.jobs.reconnects()}`,
  );

  constructor() {
    this.form.controls.scope.valueChanges
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((scope) => this.switchUnit(unitOf(scope)));
    this.route.queryParamMap
      .pipe(map(parseAnalyseParams), takeUntilDestroyed(this.destroyRef))
      .subscribe(({ sender, count }) => {
        if (sender) this.form.patchValue({ scope: 'sender', sender });
        if (count !== null) {
          this.countFromLink = true;
          this.setCount(count);
        }
      });
    this.settings
      .getSettings()
      .pipe(
        catchError(() => of(null)),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((s) => {
        if (!s) return;
        this.emailDefault = s.analysisDefaultCount;
        if (
          this.unit === 'emails' &&
          !this.countFromLink &&
          this.form.controls.preset.value === null
        ) {
          this.setCount(s.analysisDefaultCount);
        }
      });

    merge(
      this.form.valueChanges.pipe(
        startWith(null),
        map(() => selectionOf(this.form.getRawValue())),
        distinctUntilChanged((a, b) => JSON.stringify(a) === JSON.stringify(b)),
      ),
      this.refreshPreview.pipe(map(() => this.selection())),
    )
      .pipe(
        tap(() => this.previewLoading.set(true)),
        debounceTime(PREVIEW_DEBOUNCE_MS),
        switchMap((selection) =>
          selection ? this.analysis.preview(selection).pipe(catchError(() => of(null))) : of(null),
        ),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((preview) => {
        this.preview.set(preview);
        this.previewLoading.set(false);
      });

    this.form.controls.sender.valueChanges
      .pipe(
        debounceTime(SENDER_SEARCH_DEBOUNCE_MS),
        map(cleanSearch),
        distinctUntilChanged(),
        switchMap((search) =>
          search && this.showSender()
            ? this.senders
                .list({
                  search,
                  page: 1,
                  pageSize: SENDER_OPTIONS,
                  sort: 'total',
                  dir: 'desc',
                  kinds: [],
                })
                .pipe(
                  map((page) => page.items.map((s) => s.address)),
                  catchError(() => of([])),
                )
            : of([]),
        ),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((options) => this.senderOptions.set(options));

    this.reloadRuns
      .pipe(
        switchMap(() =>
          forkJoin({
            active: this.analysis.listRuns(true, ACTIVE_RUNS_LIMIT),
            finished: this.analysis.listRuns(
              false,
              FINISHED_RUNS_SHOWN,
              this.failedOnly() ? 'failed' : undefined,
            ),
          }).pipe(catchError(() => of(null))),
        ),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((runs) => {
        this.runsFailed.set(!runs);
        if (!runs) return;
        const ids = new Set(runs.active.map((r) => r.id));
        const ended = this.active().some((r) => !ids.has(r.id));
        this.active.set(queueOrder(runs.active));
        this.queueTruncated.set(runs.active.length >= ACTIVE_RUNS_LIMIT);
        this.finished.set(runs.finished);
        if (ended) this.refreshPreview.next();
        // A cancel is done once its run has left the queue.
        this.cancelling.update((c) => new Set([...c].filter((id) => ids.has(id))));
      });
    effect(() => {
      this.runsKey();
      this.failedOnly();
      untracked(() => this.loadRuns());
    });
  }

  loadRuns(): void {
    this.reloadRuns.next();
  }

  start(): void {
    const selection = this.selection();
    if (!selection || this.starting()) return;
    this.starting.set(true);
    this.analysis
      .start(selection)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (run) => {
          this.starting.set(false);
          this.noChatModel.set(false);
          this.active.update((runs) => [...runs.filter((r) => r.id !== run.id), run]);
          this.refreshPreview.next();
          this.loadRuns();
        },
        // The error interceptor shows the message; a 409 also links to Settings.
        error: (error: unknown) => {
          this.starting.set(false);
          this.noChatModel.set(error instanceof HttpErrorResponse && error.status === 409);
        },
      });
  }

  /** Re-analyses a finished run's suggestions; the results wait on Review next to the current ones. */
  reanalyse(run: AnalysisRunDto): void {
    if (this.runActive() || run.messagesCovered > MAX_COMPARE) return;
    // The API takes the run's suggestions still to decide, so the run's count is a ceiling.
    confirmCompareRun(this.dialog, this.analysis, { runId: run.id }, run.messagesCovered, true)
      .pipe(takeUntilDestroyed(this.destroyRef))
      // The error interceptor shows the server's problem detail (a 409 among them).
      .subscribe({
        next: (started) => {
          this.active.update((runs) => [...runs.filter((r) => r.id !== started.id), started]);
          this.loadRuns();
        },
        error: () => this.loadRuns(),
      });
  }

  cancel(run: AnalysisRunDto): void {
    openConfirm(this.dialog, {
      title: 'Cancel run?',
      message: 'The run stops after its current group. Suggestions already made are kept.',
      confirm: 'Cancel run',
    })
      .pipe(
        filter((confirmed) => confirmed),
        tap(() => this.cancelling.update((c) => new Set(c).add(run.id))),
        switchMap(() => this.analysis.cancel(run.id)),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe({
        next: () => this.loadRuns(),
        error: () => {
          this.cancelling.update((c) => new Set([...c].filter((id) => id !== run.id)));
          this.loadRuns();
        },
      });
  }

  /**
   * Continues a failed or stalled run; its new job reports progress through the jobs hub. The error interceptor shows
   * a 409 (not resumable, no chat model) as a snackbar.
   */
  resume(run: AnalysisRunDto): void {
    if (this.resuming().has(run.id)) return;
    this.resuming.update((r) => new Set(r).add(run.id));
    this.analysis
      .resume(run.id)
      .pipe(
        finalize(() => this.resuming.update((r) => new Set([...r].filter((id) => id !== run.id)))),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe({ next: () => this.loadRuns(), error: () => this.loadRuns() });
  }

  /**
   * Emails and senders keep their own count: switching to top senders starts at its default, and switching back
   * restores the email count (the settings default until one was chosen).
   */
  private switchUnit(unit: CountUnit): void {
    if (unit === this.unit) return;
    this.savedCounts[this.unit] = countOf(this.form.getRawValue());
    this.unit = unit;
    const custom = this.form.controls.customCount;
    custom.setValidators([
      Validators.min(1),
      Validators.max(unit === 'senders' ? MAX_TOP_SENDERS : MAX_COUNT),
      Validators.pattern(/^\d+$/),
    ]);
    const fallback = unit === 'senders' ? DEFAULT_TOP_SENDERS : this.emailDefault;
    this.setCount(this.savedCounts[unit] ?? fallback);
    custom.updateValueAndValidity();
  }

  /** Selects the matching preset chip, or "Custom" with the value; `null` clears the count. */
  private setCount(count: number | null): void {
    if (count === null) {
      this.form.patchValue({ preset: null, customCount: null });
    } else if (COUNT_PRESETS.includes(count)) {
      this.form.patchValue({ preset: count });
    } else {
      this.form.patchValue({ preset: 'custom', customCount: count });
    }
  }
}
