import {
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { EMPTY, expand, finalize, Observable, reduce, switchMap } from 'rxjs';
import {
  CreateExternalReviewsRequest,
  ExternalReviewDto,
  isOpenReview,
  MAX_CLAUDE_TARGETS,
  sentMessage,
} from '../core/claude.models';
import { ClaudeService } from '../core/claude.service';
import { ClaudeReviewerMode } from '../settings/settings.models';
import {
  DEFAULT_FLAG_LABELS,
  FlagLabels,
  MAX_GROUP_PAGE_SIZE,
  pendingClaudeRequest,
  ReviewGroupDto,
} from './review.models';
import { ReviewService } from './review.service';

/**
 * The verdict chip: "Agrees", "Suggests <label> [flags]" or "Needs a human". An alternative that
 * changes the document type adds "Document type: <label>" ("none" when it clears it).
 */
export function verdictText(item: ExternalReviewDto, labels: FlagLabels): string {
  switch (item.verdict) {
    case 'agree':
      return 'Agrees';
    case 'needs_human':
      return 'Needs a human';
    case 'alternative': {
      if (item.targetType === 'filter_finding') return 'Suggests other criteria';
      if (item.targetType === 'label_plan') return 'Suggests another structure';
      const flags = [
        item.verdictNeedsAction ? labels.action : null,
        item.verdictToBeDeleted ? labels.delete : null,
      ].filter((f): f is string => !!f);
      const text = [`Suggests ${item.verdictTopicLabel ?? ''}`, ...flags.map((f) => `[${f}]`)].join(
        ' ',
      );
      return item.verdictDocumentTypeSet
        ? `${text} · Document type: ${item.verdictDocumentTypeLabel || 'none'}`
        : text;
    }
    default:
      return '';
  }
}

/**
 * A label plan alternative's paths as an indented tree: parents before children (siblings by name), each label's own
 * name at its depth, and any parent Claude left out added so no label shows without its ancestors.
 */
export function structureRows(paths: readonly string[]): { name: string; depth: number }[] {
  const all = new Map<string, string[]>();
  for (const path of paths) {
    const parts = path.split('/');
    for (let i = 1; i <= parts.length; i++) all.set(parts.slice(0, i).join('/'), parts.slice(0, i));
  }
  return [...all.values()]
    .sort((a, b) => {
      for (let i = 0; i < Math.min(a.length, b.length); i++) {
        const c = a[i].localeCompare(b[i]);
        if (c !== 0) return c;
      }
      return a.length - b.length;
    })
    .map((parts) => ({ name: parts[parts.length - 1], depth: parts.length - 1 }));
}

export function resolutionText(item: ExternalReviewDto): string {
  return item.resolution === 'accepted_claude'
    ? "Accepted Claude's"
    : item.resolution === 'dismissed'
      ? 'Kept local'
      : '';
}

/**
 * "Send to Claude" for one card or row and its Claude review item beside the local suggestion:
 * queued (Cancel), running, reviewed (Accept Claude's / Keep local), unavailable (Retry), resolved.
 * Hidden when Claude review is off, unless `showWhenOff`: then "Send to Claude" is disabled with a tooltip. Every changed item is emitted for the page to patch in place.
 */
@Component({
  selector: 'app-claude-verdict',
  imports: [MatButtonModule, MatIconModule, MatProgressSpinnerModule, MatTooltipModule],
  template: `
    @if (mode() !== 'off' || showWhenOff()) {
      @let r = shown();
      <div class="flex flex-col gap-1 text-sm" data-testid="claude-panel">
        @if (r) {
          <div class="flex flex-wrap items-center gap-2" role="status">
            @switch (r.status) {
              @case ('queued') {
                <span data-testid="claude-queued">Queued for Claude</span>
                <button
                  mat-button
                  type="button"
                  [disabled]="disabled()"
                  (click)="act('cancel', r)"
                  data-testid="claude-cancel"
                >
                  Cancel
                </button>
              }
              @case ('running') {
                <mat-spinner diameter="16" aria-hidden="true" />
                <span data-testid="claude-running">Claude is reviewing</span>
              }
              @case ('reviewed') {
                <span class="chip" data-testid="claude-verdict">Claude: {{ verdict() }}</span>
                @if (r.resolution === 'none') {
                  <button
                    mat-stroked-button
                    type="button"
                    [disabled]="disabled() || r.verdict === 'needs_human'"
                    (click)="act('accept', r)"
                    data-testid="claude-accept"
                  >
                    Accept Claude's
                  </button>
                  <button
                    mat-button
                    type="button"
                    [disabled]="disabled()"
                    (click)="act('dismiss', r)"
                    data-testid="claude-dismiss"
                  >
                    Keep local
                  </button>
                } @else {
                  <span class="muted-chip" data-testid="claude-resolution">{{ resolution() }}</span>
                }
              }
              @case ('unavailable') {
                <span class="error" data-testid="claude-error">{{
                  r.error || 'Claude is unavailable.'
                }}</span>
                <button
                  mat-button
                  type="button"
                  [disabled]="disabled()"
                  (click)="act('retry', r)"
                  data-testid="claude-retry"
                >
                  Retry
                </button>
              }
            }
          </div>
          @if (r.status === 'reviewed' && r.verdict === 'alternative') {
            @if (r.verdictFilterCriteria) {
              <p class="reasoning m-0" data-testid="claude-criteria">
                Criteria: <code>{{ r.verdictFilterCriteria }}</code>
              </p>
            }
            @if (structure().length) {
              <ul
                class="m-0 list-none p-0"
                aria-label="Claude's label structure"
                data-testid="claude-structure"
              >
                @for (row of structure(); track $index) {
                  <li class="reasoning" [style.padding-left.rem]="row.depth * 1.25">
                    {{ row.name }}
                  </li>
                }
              </ul>
            }
          }
          @if (r.status === 'reviewed' && r.reasoning) {
            <p class="reasoning m-0" data-testid="claude-reasoning">{{ r.reasoning }}</p>
          }
        }
        <!-- A disabled button shows no tooltip; the wrapper does. -->
        <span class="self-start" [matTooltip]="sendTooltip()" data-testid="claude-send-tooltip">
          <button
            mat-button
            type="button"
            [disabled]="disabled() || open() || !sendable() || mode() === 'off'"
            (click)="send()"
            data-testid="claude-send"
          >
            Send to Claude
          </button>
        </span>
      </div>
    }
  `,
  styles: `
    .chip {
      background: var(--mat-sys-tertiary-container);
      color: var(--mat-sys-on-tertiary-container);
      border-radius: 0.5rem;
      padding: 0.125rem 0.5rem;
      overflow-wrap: anywhere;
    }
    .muted-chip {
      border: 1px solid var(--mat-sys-outline-variant);
      color: var(--mat-sys-on-surface-variant);
      border-radius: 0.5rem;
      padding: 0 0.375rem;
    }
    .reasoning {
      overflow-wrap: anywhere;
    }
    .error {
      color: var(--mat-sys-error);
      overflow-wrap: anywhere;
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ClaudeVerdict {
  private readonly claude = inject(ClaudeService);
  private readonly snackBar = inject(MatSnackBar);
  private readonly destroyRef = inject(DestroyRef);

  readonly mode = input.required<ClaudeReviewerMode>();
  /** What "Send to Claude" queues. */
  readonly request = input.required<CreateExternalReviewsRequest>();
  readonly review = input<ExternalReviewDto | null | undefined>(null);
  readonly labels = input<FlagLabels>(DEFAULT_FLAG_LABELS);
  readonly busy = input(false);
  /** Only pending suggestions can be sent. */
  readonly sendable = input(true);
  /** Shows the disabled "Send to Claude" when Claude review is off, instead of nothing. */
  readonly showWhenOff = input(false);
  readonly changed = output<ExternalReviewDto>();

  readonly working = signal(false);
  readonly disabled = computed(() => this.busy() || this.working());
  /** A cancelled item is the same as none. */
  readonly shown = computed(() => {
    const r = this.review();
    return r && r.status !== 'cancelled' ? r : null;
  });
  readonly open = computed(() => isOpenReview(this.review()));
  readonly verdict = computed(() => {
    const r = this.shown();
    return r ? verdictText(r, this.labels()) : '';
  });
  readonly structure = computed(() => structureRows(this.shown()?.alternativeStructure ?? []));
  readonly sendTooltip = computed(() =>
    this.mode() === 'off'
      ? 'Turn on Claude review in Settings to send this'
      : this.open()
        ? 'Claude already has this; decide on its review first'
        : '',
  );
  readonly resolution = computed(() => {
    const r = this.shown();
    return r ? resolutionText(r) : '';
  });

  send(): void {
    this.run(this.claude.createReviews(this.request()), (r) => {
      this.snackBar.open(sentMessage(r), 'Dismiss', { duration: 4000 });
      r.items.forEach((item) => this.changed.emit(item));
    });
  }

  act(action: 'cancel' | 'accept' | 'dismiss' | 'retry', item: ExternalReviewDto): void {
    this.run(this.claude[action](item.id), (r) => this.changed.emit(r));
  }

  private run<T>(request: Observable<T>, next: (value: T) => void): void {
    if (this.disabled()) return;
    this.working.set(true);
    request
      .pipe(
        finalize(() => this.working.set(false)),
        takeUntilDestroyed(this.destroyRef),
      )
      // The error interceptor shows the server's problem detail.
      .subscribe({ next, error: () => undefined });
  }
}

/** The sender header's Claude actions: send every pending group, and in Desktop mode copy the prompt. */
@Component({
  selector: 'app-claude-sender-actions',
  imports: [MatButtonModule],
  template: `
    @if (mode() !== 'off') {
      <button
        mat-stroked-button
        type="button"
        [disabled]="busy() || working()"
        (click)="sendPending()"
        data-testid="claude-send-pending"
      >
        Send all pending to Claude
      </button>
      @if (mode() === 'claude_desktop') {
        <button
          mat-button
          type="button"
          [disabled]="copying()"
          (click)="copyPrompt()"
          data-testid="claude-copy-prompt"
        >
          Copy prompt for Claude Desktop
        </button>
      }
    }
  `,
  styles: `
    :host {
      display: contents;
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ClaudeSenderActions {
  private readonly claude = inject(ClaudeService);
  private readonly review = inject(ReviewService);
  private readonly snackBar = inject(MatSnackBar);
  private readonly destroyRef = inject(DestroyRef);

  readonly mode = input.required<ClaudeReviewerMode>();
  readonly address = input.required<string>();
  readonly busy = input(false);
  readonly changed = output<ExternalReviewDto>();

  readonly working = signal(false);
  readonly copying = signal(false);

  /** Reads the sender's pending groups (all pages, up to the API's limit) and queues them. */
  sendPending(): void {
    if (this.working()) return;
    const address = this.address();
    this.working.set(true);
    let total = 0;
    this.pendingGroups(address)
      .pipe(
        switchMap((groups) => {
          total = groups.total;
          const request = pendingClaudeRequest(address, groups.groups);
          if (!request) {
            this.snackBar.open('Nothing pending to send.', 'Dismiss', { duration: 4000 });
            return EMPTY;
          }
          return this.claude.createReviews(request);
        }),
        finalize(() => this.working.set(false)),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe({
        next: (r) => {
          const limited =
            total > MAX_CLAUDE_TARGETS
              ? ` Only the largest ${MAX_CLAUDE_TARGETS} groups were sent.`
              : '';
          this.snackBar.open(sentMessage(r) + limited, 'Dismiss', { duration: 6000 });
          r.items.forEach((item) => this.changed.emit(item));
        },
        error: () => undefined,
      });
  }

  copyPrompt(): void {
    if (this.copying()) return;
    this.copying.set(true);
    void this.claude
      .copyReviewPrompt()
      .then((result) => {
        if (result === 'copied')
          this.snackBar.open('Review prompt copied', undefined, { duration: 2000 });
        else if (result === 'copy_failed')
          this.snackBar.open('Could not copy the review prompt', 'Close');
      })
      .finally(() => this.copying.set(false));
  }

  private pendingGroups(address: string): Observable<{ groups: ReviewGroupDto[]; total: number }> {
    const size = MAX_GROUP_PAGE_SIZE;
    const pages = Math.ceil(MAX_CLAUDE_TARGETS / size);
    const page = (n: number) => this.review.sender(address, 'pending', n, size);
    return page(1).pipe(
      expand((d) =>
        d.page < pages && d.page * d.pageSize < d.totalGroups ? page(d.page + 1) : EMPTY,
      ),
      reduce((acc, d) => ({ groups: acc.groups.concat(d.groups), total: d.totalGroups }), {
        groups: [] as ReviewGroupDto[],
        total: 0,
      }),
    );
  }
}
