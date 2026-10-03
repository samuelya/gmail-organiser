import { DatePipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  inject,
  input,
  linkedSignal,
  signal,
} from '@angular/core';
import { takeUntilDestroyed, toObservable, toSignal } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatChipsModule } from '@angular/material/chips';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { catchError, finalize, map, of, startWith, switchMap } from 'rxjs';
import {
  UnsubscribeInfo,
  UnsubscribeMethod,
  unsubscribeFailedMessage,
  unsubscribeHost,
  unsubscribeUrl,
} from './clean-up.models';
import { CleanUpService } from './clean-up.service';

type ManualMethod = Exclude<UnsubscribeMethod, 'one_click'>;

/** The info for one address; `failed` when it could not be read (the click then retries). */
interface LoadedInfo {
  address: string;
  info: UnsubscribeInfo | null;
  failed: boolean;
}

/**
 * The sender header's Unsubscribe (epic #24 Q4): one-click is POSTed by the api; a link opens in a new
 * tab and a `mailto:` goes to the mail client through a real anchor, after which the user marks it
 * done. Never navigates this window, never labels or trashes mail, and shows only the target's host.
 */
@Component({
  selector: 'app-unsubscribe-button',
  imports: [
    DatePipe,
    MatButtonModule,
    MatChipsModule,
    MatIconModule,
    MatProgressSpinnerModule,
    MatTooltipModule,
  ],
  template: `
    <div class="flex flex-wrap items-center gap-2">
      @let i = info();
      @let u = url();
      @if (loading()) {
        <button mat-stroked-button type="button" disabled data-testid="unsubscribe">
          Unsubscribe
        </button>
      } @else if (failed()) {
        <button mat-stroked-button type="button" (click)="retry()" data-testid="unsubscribe">
          Unsubscribe
        </button>
      } @else if (!u) {
        <span tabindex="0" matTooltip="No unsubscribe header" data-testid="unsubscribe-none">
          <button mat-stroked-button type="button" disabled data-testid="unsubscribe">
            Unsubscribe
          </button>
        </span>
      } @else if (i?.method === 'one_click') {
        <button
          mat-stroked-button
          type="button"
          [disabled]="sending()"
          [attr.aria-busy]="sending()"
          (click)="oneClick()"
          data-testid="unsubscribe"
        >
          @if (sending()) {
            <mat-spinner
              diameter="18"
              aria-label="Unsubscribing"
              data-testid="unsubscribe-spinner"
            />
          }
          Unsubscribe
        </button>
        @if (fallback()) {
          <a
            mat-stroked-button
            [href]="u.href"
            target="_blank"
            rel="noopener noreferrer"
            [attr.aria-label]="'Open the unsubscribe link on ' + host() + ' in a new tab'"
            (click)="opened.set('link')"
            data-testid="unsubscribe-fallback"
          >
            Open the link
          </a>
        }
      } @else {
        <a
          mat-stroked-button
          [href]="u.href"
          [attr.target]="i?.method === 'link' ? '_blank' : null"
          [attr.rel]="i?.method === 'link' ? 'noopener noreferrer' : null"
          [attr.aria-label]="
            i?.method === 'link'
              ? 'Unsubscribe on ' + host() + ' in a new tab'
              : 'Unsubscribe by email to ' + host()
          "
          (click)="opened.set(i?.method === 'mailto' ? 'mailto' : 'link')"
          data-testid="unsubscribe"
        >
          Unsubscribe
        </a>
      }
      @if (u) {
        <span class="muted text-sm" data-testid="unsubscribe-host">{{ host() }}</span>
      }
      @if (opened(); as method) {
        <button
          mat-stroked-button
          type="button"
          [disabled]="marking()"
          (click)="mark(method)"
          data-testid="unsubscribe-mark"
        >
          Mark as unsubscribed
        </button>
      }
      @if (i?.unsubscribedAt; as at) {
        <mat-chip-set aria-label="Unsubscribe status">
          <mat-chip disableRipple data-testid="unsubscribed-chip">
            <mat-icon matChipAvatar aria-hidden="true">unsubscribe</mat-icon>
            Unsubscribed {{ at | date: 'mediumDate' }}
          </mat-chip>
        </mat-chip-set>
      }
    </div>
  `,
  styles: `
    .muted {
      color: var(--mat-sys-on-surface-variant);
    }
    mat-spinner {
      display: inline-block;
      margin-right: 0.5rem;
      vertical-align: middle;
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class UnsubscribeButton {
  private readonly api = inject(CleanUpService);
  private readonly snackBar = inject(MatSnackBar);
  private readonly destroyRef = inject(DestroyRef);

  readonly address = input.required<string>();
  /** Bumped after a send, a mark or a retry: the info (and the chip) is read again. */
  private readonly version = signal(0);

  private readonly loaded = toSignal(
    toObservable(computed(() => ({ address: this.address(), version: this.version() }))).pipe(
      switchMap(({ address }) =>
        this.api.unsubscribeInfo(address).pipe(
          map((info): LoadedInfo => ({ address, info, failed: false })),
          catchError(() => of<LoadedInfo>({ address, info: null, failed: true })),
          startWith(null),
        ),
      ),
    ),
    { initialValue: null },
  );

  readonly loading = computed(() => this.loaded()?.address !== this.address());
  readonly failed = computed(() => !this.loading() && !!this.loaded()?.failed);
  readonly info = computed(() => (this.loading() ? null : (this.loaded()?.info ?? null)));
  readonly url = computed(() => unsubscribeUrl(this.info()));
  readonly host = computed(() => {
    const url = this.url();
    return url ? unsubscribeHost(url) : '';
  });

  readonly sending = signal(false);
  readonly marking = signal(false);
  /** The one-click failed: offer its URL as a link. Reset for another sender. */
  readonly fallback = linkedSignal({ source: this.address, computation: () => false });
  /** The link or `mailto:` the user opened, waiting to be marked. Reset for another sender. */
  readonly opened = linkedSignal<string, ManualMethod | null>({
    source: this.address,
    computation: () => null,
  });

  retry(): void {
    this.version.update((v) => v + 1);
  }

  oneClick(): void {
    const address = this.address();
    this.sending.set(true);
    this.api
      .unsubscribe(address)
      .pipe(
        finalize(() => this.sending.set(false)),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe({
        next: (result) => {
          if (result.status === 'done') {
            this.snackBar.open('Unsubscribed', 'Dismiss', { duration: 4000 });
            this.retry();
            return;
          }
          this.snackBar.open(unsubscribeFailedMessage(result), 'Dismiss', { duration: 8000 });
          if (this.address() === address) this.fallback.set(true);
        },
        // The error interceptor shows API errors.
        error: () => undefined,
      });
  }

  mark(method: ManualMethod): void {
    const address = this.address();
    this.marking.set(true);
    this.api
      .markUnsubscribed(address, method)
      .pipe(
        finalize(() => this.marking.set(false)),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe({
        next: () => {
          this.snackBar.open('Marked as unsubscribed', 'Dismiss', { duration: 4000 });
          if (this.address() === address) this.opened.set(null);
          this.retry();
        },
        error: () => undefined,
      });
  }
}
