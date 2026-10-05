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
  FINISHED_RUNS_SHOWN,
  GroupingPreviewDto,
  MAX_SENDER_LENGTH,
  parseAnalyseParams,
  queueOrder,
  SCOPE_OPTIONS,
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

interface FormValue {
  scope: AnalysisScope;
  sender: string;
  preset: CountPreset | null;
  customCount: number | null;
}

/** The selection to preview and start, or `null` while it is incomplete or invalid. */
export function selectionOf(v: FormValue): AnalysisSelection | null {
  const count = v.preset === 'custom' ? v.customCount : v.preset;
  if (count === null || !Number.isInteger(count) || count < 1 || count > MAX_COUNT) return null;
  if (v.scope !== 'sender') return { scope: v.scope, count };
  const sender = v.sender.trim();
  if (!sender || sender.length > MAX_SENDER_LENGTH) return null;
  return { scope: 'sender', senderAddress: sender, count };
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

  readonly presets = COUNT_PRESETS;
  readonly maxCount = MAX_COUNT;
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
  readonly isSender = computed(() => this.formValue().scope === 'sender');
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
  private readonly hubRunActive = injectAnalysisRunActive();
  /** A run is queued or running (by the hub or the last list): no re-analysis can start. */
  readonly runActive = computed(() => this.hubRunActive() || this.active().length > 0);

  /** Run lists reload when an analysis job appears or changes status, or after a reconnect. */
  private readonly runsKey = computed(
    () => `${analysisJobsKey(this.jobs.jobs())}|${this.jobs.reconnects()}`,
  );

  constructor() {
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
        if (s && !this.countFromLink && this.form.controls.preset.value === null) {
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
          search && this.isSender()
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
            finished: this.analysis.listRuns(false, FINISHED_RUNS_SHOWN),
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

  /** Selects the matching preset chip, or "Custom" with the value. */
  private setCount(count: number): void {
    if (COUNT_PRESETS.includes(count)) {
      this.form.patchValue({ preset: count });
    } else {
      this.form.patchValue({ preset: 'custom', customCount: count });
    }
  }
}
