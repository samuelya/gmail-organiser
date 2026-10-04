import { DatePipe, DecimalPipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  effect,
  inject,
  linkedSignal,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import {
  AbstractControl,
  FormControl,
  FormGroup,
  FormGroupDirective,
  ReactiveFormsModule,
  Validators,
} from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatChipsModule } from '@angular/material/chips';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatSortModule, Sort } from '@angular/material/sort';
import { MatTableModule } from '@angular/material/table';
import { MatTooltipModule } from '@angular/material/tooltip';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import {
  catchError,
  EMPTY,
  filter,
  map,
  merge,
  Observable,
  of,
  Subject,
  switchMap,
  timer,
} from 'rxjs';
import { openConfirm } from '../core/confirm-dialog';
import { isActiveJob, JobDto, JobStatus, newerJob } from '../core/jobs.models';
import { JobsService } from '../core/jobs.service';
import { PagedDto } from '../core/paging.models';
import { PageHeader } from '../layout/page-header';
import { coveringDomain } from '../settings/settings.models';
import { SettingsService } from '../settings/settings.service';
import { SenderProgress } from './sender-progress.component';
import {
  analysedPercent,
  cleanSearch,
  DEFAULT_SENDER_QUERY,
  hasControlChars,
  lastPage,
  MAX_SEARCH_LENGTH,
  normaliseFetchTarget,
  PAGE_SIZES,
  parseSenderQuery,
  relativeTime,
  SENDER_FETCH_JOB,
  SenderDto,
  SenderQuery,
  senderQueryParams,
  SenderSort,
} from './senders.models';
import { SendersService } from './senders.service';

export const SEARCH_DEBOUNCE_MS = 300;

/** A resume or cancel sent for a job, until the job leaves `status`. */
interface PendingAction {
  action: 'resume' | 'cancel';
  status: JobStatus;
}

/** `/senders`: the paged senders list with per-sender progress and "Fetch all from sender". */
@Component({
  selector: 'app-senders-page',
  imports: [
    DatePipe,
    DecimalPipe,
    MatButtonModule,
    MatCardModule,
    MatChipsModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatPaginatorModule,
    MatProgressBarModule,
    MatSortModule,
    MatTableModule,
    MatTooltipModule,
    PageHeader,
    ReactiveFormsModule,
    RouterLink,
    SenderProgress,
  ],
  templateUrl: './senders-page.component.html',
  styles: `
    .muted {
      color: var(--mat-sys-on-surface-variant);
    }
    .address {
      font: var(--mat-sys-body-small);
      color: var(--mat-sys-on-surface-variant);
    }
    .analysed-bar {
      width: 6rem;
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SendersPage {
  private readonly senders = inject(SendersService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly dialog = inject(MatDialog);
  private readonly snackBar = inject(MatSnackBar);
  private readonly destroyRef = inject(DestroyRef);
  private readonly jobs = inject(JobsService);
  private readonly settings = inject(SettingsService);
  private readonly requests = new Subject<SenderQuery>();
  /** Drops a pending debounced search: the URL changed or the box was cleared. */
  private readonly searchReset = new Subject<void>();

  readonly columns = ['sender', 'domain', 'total', 'analysed', 'lastSeen', 'actions'];
  readonly pageSizes = PAGE_SIZES;
  private readonly loadedSettings = toSignal(
    this.settings.getSettings().pipe(catchError(() => of(null))),
    { initialValue: null },
  );
  /** "Analyse" links carry the default count; without settings the Analyse page picks it. */
  readonly analyseCount = computed(() => this.loadedSettings()?.analysisDefaultCount ?? null);
  /** `protection.allowlistedDomains` as last saved; `null` until loaded, which disables the domain action. */
  readonly allowlistedDomains = linkedSignal(
    () => this.loadedSettings()?.protection?.allowlistedDomains ?? null,
  );
  /** The domain list is saved whole, so one change at a time. */
  readonly domainSaving = signal(false);
  /** The URL is the source of truth for search, page, size and sort. */
  readonly query = toSignal(this.route.queryParamMap.pipe(map(parseSenderQuery)), {
    initialValue: DEFAULT_SENDER_QUERY,
  });
  readonly result = signal<PagedDto<SenderDto> | null>(null);
  readonly loading = signal(false);
  readonly loadFailed = signal(false);
  /** Addresses or domains whose fetch request is in flight. */
  readonly starting = signal<ReadonlySet<string>>(new Set());
  /** Addresses with an allowlist change in flight. */
  readonly allowlisting = signal<ReadonlySet<string>>(new Set());
  /** Resume or cancel per job id, until the job's status changes. */
  private readonly pending = signal<ReadonlyMap<string, PendingAction>>(new Map());

  readonly search = new FormControl('', {
    nonNullable: true,
    validators: [Validators.maxLength(MAX_SEARCH_LENGTH), searchValidator],
  });
  readonly targetForm = new FormGroup({
    target: new FormControl('', {
      nonNullable: true,
      validators: [fetchTargetValidator],
    }),
  });
  readonly target = this.targetForm.controls.target;
  /** Resetting through the directive also clears its submitted state, so no error shows after a fetch. */
  private readonly targetFormDirective = viewChild(FormGroupDirective);
  /** Active sender fetch jobs seen here; each one that finishes is dropped and moves `finishes`. */
  private readonly watched = signal<ReadonlySet<string>>(new Set());
  private readonly finishes = signal(0);

  /** One stable object per sender; only a new page replaces them, so job ticks don't re-render rows. */
  readonly rows = computed(() =>
    (this.result()?.items ?? []).map((sender) => ({ sender, percent: analysedPercent(sender) })),
  );
  readonly trackRow = (_: number, row: { sender: SenderDto }) => row.sender.address;

  /** The live state of each row's fetch job, by address; absent once it finished. */
  readonly rowJobs = computed(() => {
    const jobs = new Map<string, JobDto>();
    for (const { sender } of this.rows()) {
      const job = this.liveJob(sender.activeFetchJob);
      if (job && isActiveJob(job)) jobs.set(sender.address, job);
    }
    return jobs;
  });

  /** Jobs shown on more than one visible row: only a domain fetch covers several senders. */
  readonly sharedJobs = computed(() => {
    const seen = new Set<string>();
    const shared = new Set<string>();
    for (const job of this.rowJobs().values()) {
      if (seen.has(job.id)) shared.add(job.id);
      seen.add(job.id);
    }
    return shared;
  });

  constructor() {
    this.requests
      .pipe(
        switchMap((query) =>
          this.senders.list(query).pipe(
            map((page) => page as PagedDto<SenderDto> | null),
            catchError(() => of(null)),
          ),
        ),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((page) => {
        this.loading.set(false);
        this.loadFailed.set(!page);
        if (!page) return;
        const query = this.query();
        const last = lastPage(page.total, query.pageSize);
        // A bookmark or back/forward past the end: go to the last page instead of an empty one.
        if (page.items.length === 0 && query.page > last) {
          this.navigate({ page: last }, true);
          return;
        }
        this.result.set(page);
      });
    // A watched job that finished moved the counts on this page: refetch once and stop watching it.
    effect(() => {
      const active = [
        ...[...this.rowJobs().values()].map((j) => j.id),
        ...this.jobs.activeJobs().flatMap((j) => (j.type === SENDER_FETCH_JOB ? j.id : [])),
      ];
      const watched = untracked(this.watched);
      const next = new Set([...watched, ...active]);
      let finished = false;
      for (const id of next) {
        const job = this.jobs.job(id);
        if (job && !isActiveJob(job)) finished = next.delete(id);
      }
      if (next.size !== watched.size || [...next].some((id) => !watched.has(id))) {
        this.watched.set(next);
      }
      if (finished) untracked(() => this.finishes.update((n) => n + 1));
    });
    effect(() => {
      const query = this.query();
      this.finishes();
      this.jobs.reconnects();
      untracked(() => this.load(query));
    });
    // Back/forward or a link changes the URL: show its search in the box and drop any typed one.
    effect(() => {
      const search = this.query().search;
      untracked(() => {
        this.searchReset.next();
        if (this.search.value.trim() !== search) this.search.setValue(search, { emitEvent: false });
      });
    });
    // A pending resume or cancel ends when its job changes status (or finishes).
    effect(() => {
      const pending = this.pending();
      if (pending.size === 0) return;
      const next = new Map([...pending].filter(([id, p]) => this.statusOf(id) === p.status));
      if (next.size !== pending.size) this.pending.set(next);
    });
    // Typing searches after a pause, with the box's value at that moment; a reset drops the wait.
    merge(this.search.valueChanges.pipe(map(() => true)), this.searchReset.pipe(map(() => false)))
      .pipe(
        switchMap((typed) => (typed ? timer(SEARCH_DEBOUNCE_MS) : EMPTY)),
        filter(() => this.search.valid),
        map(() => cleanSearch(this.search.value)),
        filter((search) => search !== this.query().search),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((search) => this.navigate({ search, page: 1 }));
  }

  /** Reloads the current page. */
  reload(): void {
    this.load(this.query());
  }

  onPage(event: PageEvent): void {
    const pageSize = event.pageSize;
    const page = pageSize === this.query().pageSize ? event.pageIndex + 1 : 1;
    this.navigate({ page, pageSize });
  }

  onSort(sort: Sort): void {
    const d = DEFAULT_SENDER_QUERY;
    const cleared = !sort.direction;
    this.navigate({
      sort: cleared ? d.sort : (sort.active as SenderSort),
      dir: cleared ? d.dir : sort.direction || d.dir,
      page: 1,
    });
  }

  clearSearch(): void {
    this.searchReset.next();
    this.search.setValue('', { emitEvent: false });
    this.navigate({ search: '', page: 1 });
  }

  fetchSender(sender: SenderDto): void {
    this.startFetch(sender.address);
  }

  submitTarget(): void {
    const target = normaliseFetchTarget(this.target.value);
    if (!target) {
      this.target.markAsTouched();
      return;
    }
    this.startFetch(target, () => this.targetFormDirective()?.resetForm());
  }

  /** Resumes the paused job itself, whichever target (address or domain) it fetches. */
  resume(job: JobDto): void {
    this.runPending(job, 'resume', this.jobs.resume(job.id));
  }

  /** The job has no target: a domain fetch is certain only when several rows share it. */
  cancel(job: JobDto, domainFetch: boolean): void {
    openConfirm(this.dialog, {
      title: domainFetch ? 'Cancel domain fetch?' : 'Cancel sender fetch?',
      message: domainFetch
        ? 'This fetch covers a whole domain, so it stops for every sender at that domain, at its next checkpoint. Mail already fetched stays.'
        : 'It stops at its next checkpoint. If it was started for the whole domain, it stops for every sender at that domain. Mail already fetched stays.',
      confirm: 'Cancel fetch',
    })
      .pipe(
        filter((confirmed) => confirmed),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe(() => this.runPending(job, 'cancel', this.jobs.cancel(job.id)));
  }

  /** Flips the sender's allowlist flag; the row shows the saved value once the API answers. */
  toggleAllowlist(sender: SenderDto): void {
    const address = sender.address;
    if (this.allowlisting().has(address)) return;
    this.allowlisting.update((busy) => new Set(busy).add(address));
    this.senders
      .setAllowlisted(address, !sender.allowlisted)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (saved) => {
          this.allowlisting.update((busy) => without(busy, address));
          this.result.update((page) =>
            page
              ? {
                  ...page,
                  items: page.items.map((s) =>
                    s.address === address ? { ...s, allowlisted: saved.allowlisted } : s,
                  ),
                }
              : page,
          );
        },
        // The error interceptor shows why; the row keeps its flag.
        error: () => this.allowlisting.update((busy) => without(busy, address)),
      });
  }

  /** The listed domain covering the sender's: its own or a parent; null when none is listed. */
  listedDomain(sender: SenderDto): string | null {
    return coveringDomain(sender.domain, this.allowlistedDomains() ?? []);
  }

  /** Adds the sender's domain to the allowlist, or removes the listed one covering it; then reloads. */
  toggleDomainAllowlist(sender: SenderDto): void {
    const listed = this.allowlistedDomains();
    if (!listed || this.domainSaving()) return;
    const covering = coveringDomain(sender.domain, listed);
    const next = covering ? listed.filter((d) => d !== covering) : [...listed, sender.domain];
    this.domainSaving.set(true);
    this.settings
      .saveProtection({ protection: { allowlistedDomains: next } })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (saved) => {
          this.domainSaving.set(false);
          this.allowlistedDomains.set(saved.protection?.allowlistedDomains ?? next);
          const done = covering ? `${covering} removed from` : `${sender.domain} added to`;
          this.snackBar.open(`${done} the allowlist`, undefined, { duration: 3000 });
          this.reload();
        },
        // The error interceptor shows why.
        error: () => this.domainSaving.set(false),
      });
  }

  /** A resume or cancel for this job is in flight or waiting for its status to change. */
  isPending(job: JobDto): boolean {
    return this.pending().get(job.id)?.status === job.status;
  }

  isStarting(target: string): boolean {
    return this.starting().has(target);
  }

  /** The toolbar's normalised target is being posted. */
  targetStarting(): boolean {
    const target = normaliseFetchTarget(this.target.value);
    return !!target && this.isStarting(target);
  }

  lastSeen(iso: string): string {
    return relativeTime(iso);
  }

  private startFetch(target: string, done?: () => void): void {
    if (this.isStarting(target)) return;
    this.starting.update((targets) => new Set(targets).add(target));
    this.senders
      .fetchFromSender(target)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: ({ created }) => {
          this.starting.update((targets) => without(targets, target));
          done?.();
          this.snackBar.open(
            created
              ? `Fetching all mail from ${target}. It runs after any fetch already in progress.`
              : `The fetch from ${target} is already active; it continues.`,
            'Dismiss',
            { duration: 6000 },
          );
          this.reload();
        },
        // The error interceptor shows the server's problem detail.
        error: () => this.starting.update((targets) => without(targets, target)),
      });
  }

  private runPending(
    job: JobDto,
    action: PendingAction['action'],
    request: Observable<void>,
  ): void {
    if (this.isPending(job)) return;
    this.pending.update((current) => new Map(current).set(job.id, { action, status: job.status }));
    request.pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      // The error interceptor shows the server's problem detail.
      error: () => this.pending.update((current) => withoutKey(current, job.id)),
    });
  }

  /** The newer of a job as the API listed it and as the hub last sent it. */
  private liveJob(held: JobDto | null): JobDto | null {
    return held ? newerJob(held, this.jobs.job(held.id)) : null;
  }

  private statusOf(id: string): JobStatus | undefined {
    const held = this.rows().find((r) => r.sender.activeFetchJob?.id === id)?.sender.activeFetchJob;
    return (this.liveJob(held ?? null) ?? this.jobs.job(id))?.status;
  }

  private load(query: SenderQuery): void {
    this.loading.set(true);
    this.requests.next(query);
  }

  private navigate(patch: Partial<SenderQuery>, replaceUrl = false): void {
    this.searchReset.next();
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: senderQueryParams({ ...this.query(), ...patch }),
      replaceUrl,
    });
  }
}

/** API-side rules for a sender fetch target; blank (spaces only, too) is `required`. */
function fetchTargetValidator(control: AbstractControl<string>) {
  if (!control.value.trim()) return { required: true };
  return normaliseFetchTarget(control.value) ? null : { target: true };
}

/** The API rejects control characters in a search, e.g. a tab pasted from a spreadsheet. */
function searchValidator(control: AbstractControl<string>) {
  return hasControlChars(control.value) ? { controlChars: true } : null;
}

function without<T>(set: ReadonlySet<T>, value: T): ReadonlySet<T> {
  const next = new Set(set);
  next.delete(value);
  return next;
}

function withoutKey<K, V>(map: ReadonlyMap<K, V>, key: K): ReadonlyMap<K, V> {
  const next = new Map(map);
  next.delete(key);
  return next;
}
