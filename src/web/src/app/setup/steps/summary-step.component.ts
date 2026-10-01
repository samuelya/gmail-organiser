import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { filter, switchMap } from 'rxjs';
import { SetupState } from '../setup-state';
import { SetupStatus } from '../setup.service';

export interface SummaryItem {
  label: string;
  ok: boolean;
  /** Shown instead of "Missing" when the item may stay unset. */
  optional?: boolean;
  /** The wizard step that sets it. */
  step: number;
}

/** Step indexes of the wizard, so the summary can link back. */
export interface SummarySteps {
  googleClient: number;
  gmail: number;
  ollama: number;
  models: number;
}

export function summaryItems(status: SetupStatus, steps: SummarySteps): SummaryItem[] {
  return [
    { label: 'Google OAuth client', ok: status.googleClientConfigured, step: steps.googleClient },
    {
      label: status.gmailReauthRequired ? 'Gmail connected (sign in again)' : 'Gmail connected',
      ok: status.gmailConnected,
      step: steps.gmail,
    },
    { label: 'Ollama reachable', ok: status.ollamaReachable, step: steps.ollama },
    { label: 'Chat model selected', ok: status.chatModelSelected, step: steps.models },
    {
      label: 'Embedding model selected',
      ok: status.embeddingModelSelected,
      optional: true,
      step: steps.models,
    },
  ];
}

/** Step 5: every item of `GET /api/setup/status` with a link to its step. Reloads each time it is shown. */
@Component({
  selector: 'app-summary-step',
  imports: [MatButtonModule, MatIconModule, MatProgressSpinnerModule],
  template: `
    @if (status(); as s) {
      <ul class="m-0 flex list-none flex-col gap-1 p-0" data-testid="summary">
        @for (item of items(); track item.label) {
          <li class="flex flex-wrap items-center gap-2">
            <mat-icon aria-hidden="true">{{
              item.ok ? 'check_circle' : item.optional ? 'remove_circle_outline' : 'error'
            }}</mat-icon>
            <span class="flex-1">{{ item.label }}</span>
            <span class="text-sm" [attr.data-testid]="'state-' + $index">
              {{ item.ok ? 'OK' : item.optional ? 'Optional, not set' : 'Missing' }}
            </span>
            <button
              mat-button
              type="button"
              (click)="goTo.emit(item.step)"
              [attr.aria-label]="'Go to the step for ' + item.label"
            >
              {{ item.ok ? 'Change' : 'Set up' }}
            </button>
          </li>
        }
      </ul>
      <p class="m-0 mt-3" data-testid="summary-verdict">
        @if (s.complete) {
          Setup is complete.
        } @else {
          Setup is incomplete: connect Gmail and select a chat model to start organising. You can
          finish now and come back later.
        }
      </p>
    } @else if (failed()) {
      <p class="m-0">The setup status could not be loaded.</p>
    } @else {
      <mat-spinner diameter="24" aria-label="Loading the setup status" />
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SummaryStep {
  private readonly setupState = inject(SetupState);

  /** True while the step is shown; each time it becomes true the status reloads. */
  readonly active = input(false);
  readonly steps = input.required<SummarySteps>();
  readonly goTo = output<number>();

  readonly status = signal<SetupStatus | null>(null);
  readonly failed = signal(false);
  readonly items = computed(() => {
    const status = this.status();
    return status ? summaryItems(status, this.steps()) : [];
  });

  constructor() {
    toObservable(this.active)
      .pipe(
        filter((active) => active),
        switchMap(() => this.setupState.refresh()),
        takeUntilDestroyed(),
      )
      .subscribe((status) => {
        this.status.set(status);
        this.failed.set(status === null);
      });
  }
}
