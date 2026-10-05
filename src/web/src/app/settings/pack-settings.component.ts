import { ChangeDetectionStrategy, Component, DestroyRef, inject, OnInit } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormControl, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSliderModule } from '@angular/material/slider';
import { MatSnackBar } from '@angular/material/snack-bar';
import { catchError, concatMap, debounceTime, filter, map, merge, of } from 'rxjs';
import { SettingsService } from './settings.service';
import { SettingsDto } from './settings.models';
import { NOT_SAVED } from './triage-model-settings.component';
import {
  PACK_RETRY_THRESHOLD,
  PACK_SIZE,
  PackSettings,
  percent,
  problemErrors,
  TriageUpdate,
  wholeNumber,
} from './triage-settings.models';

/** A field settles this long before it is saved. */
export const PACK_SAVE_DEBOUNCE_MS = 600;

/**
 * The Settings page's singleton packing (#380): how many one-off senders share one snippet-only
 * prompt, and below which confidence a packed answer is asked again alone. Each field is saved as
 * it changes; a failed save keeps the value shown with the reason on the field. Loads and saves
 * itself.
 */
@Component({
  selector: 'app-pack-settings',
  imports: [ReactiveFormsModule, MatFormFieldModule, MatInputModule, MatSliderModule],
  template: `
    <fieldset class="flex max-w-2xl flex-col gap-4">
      <legend class="mb-1">One-off senders</legend>
      <mat-form-field subscriptSizing="dynamic">
        <mat-label>Pack size</mat-label>
        <input
          matInput
          type="number"
          [formControl]="size"
          [min]="sizeRange.min"
          [max]="sizeRange.max"
          [step]="sizeRange.step"
          data-testid="pack-size"
        />
        @if (size.hasError('server')) {
          <mat-error data-testid="pack-size-error">{{ size.getError('server') }}</mat-error>
        } @else if (size.invalid) {
          <mat-error data-testid="pack-size-error"
            >Enter a whole number from {{ sizeRange.min }} to {{ sizeRange.max }}.</mat-error
          >
        }
        <mat-hint
          >Senders with a single email are asked about together, this many per prompt, from their
          snippets. 1 is off: each is asked alone with its body.</mat-hint
        >
      </mat-form-field>

      <div class="flex flex-col gap-1">
        <span id="pack-retry-label">Pack retry threshold</span>
        <div class="flex items-center gap-4">
          <mat-slider
            class="grow"
            [min]="retryRange.min"
            [max]="retryRange.max"
            [step]="retryRange.step"
            discrete
            [displayWith]="percent"
          >
            <input
              matSliderThumb
              [formControl]="retry"
              aria-labelledby="pack-retry-label"
              data-testid="pack-retry"
            />
          </mat-slider>
          <span class="w-12 text-right" aria-hidden="true">{{ percent(retry.value) }}</span>
        </div>
        <p class="help">
          A packed answer below this confidence is asked again alone, with the email's body.
        </p>
        @if (retry.hasError('server')) {
          <p class="help warn" role="alert" data-testid="pack-retry-error">
            {{ retry.getError('server') }}
          </p>
        }
      </div>
    </fieldset>
  `,
  styles: `
    fieldset {
      border: 0;
      margin: 0;
      padding: 0;
    }
    .help {
      font: var(--mat-sys-body-small);
      color: var(--mat-sys-on-surface-variant);
      margin: 0;
    }
    .warn {
      color: var(--mat-sys-error);
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PackSettingsSection implements OnInit {
  private readonly settingsApi = inject(SettingsService);
  private readonly snackBar = inject(MatSnackBar);
  private readonly destroyRef = inject(DestroyRef);

  readonly sizeRange = PACK_SIZE;
  readonly retryRange = PACK_RETRY_THRESHOLD;
  readonly percent = percent;

  readonly size = new FormControl<number | null>(null, [
    Validators.required,
    Validators.min(PACK_SIZE.min),
    Validators.max(PACK_SIZE.max),
    wholeNumber,
  ]);
  readonly retry = new FormControl<number | null>(null, [
    Validators.required,
    Validators.min(PACK_RETRY_THRESHOLD.min),
    Validators.max(PACK_RETRY_THRESHOLD.max),
  ]);

  constructor() {
    this.size.disable();
    this.retry.disable();
    merge(
      this.size.valueChanges.pipe(
        debounceTime(PACK_SAVE_DEBOUNCE_MS),
        filter(() => this.size.valid),
        map((value): TriageUpdate => ({ analysisPackSize: value! })),
      ),
      this.retry.valueChanges.pipe(
        debounceTime(PACK_SAVE_DEBOUNCE_MS),
        filter(() => this.retry.valid),
        map((value): TriageUpdate => ({ analysisPackRetryThreshold: value! })),
      ),
    )
      .pipe(
        concatMap((change) => this.save(change)),
        takeUntilDestroyed(),
      )
      .subscribe();
  }

  ngOnInit(): void {
    this.settingsApi
      .getSettings()
      .pipe(takeUntilDestroyed(this.destroyRef))
      // The error interceptor shows why; the section stays disabled.
      .subscribe({ next: (s) => this.load(s), error: () => undefined });
  }

  /** An older API without the fields leaves the section disabled. */
  private load(settings: SettingsDto & Partial<PackSettings>): void {
    if (
      settings.analysisPackSize === undefined ||
      settings.analysisPackRetryThreshold === undefined
    )
      return;
    this.size.setValue(settings.analysisPackSize, { emitEvent: false });
    this.retry.setValue(settings.analysisPackRetryThreshold, { emitEvent: false });
    this.size.enable({ emitEvent: false });
    this.retry.enable({ emitEvent: false });
  }

  private save(change: TriageUpdate) {
    return this.settingsApi.saveTriage(change).pipe(
      map(() => this.snackBar.open('Pack settings saved', undefined, { duration: 3000 })),
      // The value stays in its field, marked as not saved; the next change sends it again.
      catchError((error: unknown) => {
        const errors = problemErrors(error);
        const message = (key: string) => errors[key]?.[0] ?? errors['']?.[0] ?? NOT_SAVED;
        if (change.analysisPackSize !== undefined && this.size.value === change.analysisPackSize) {
          this.size.setErrors({ server: message('analysisPackSize') });
          this.size.markAsTouched();
        }
        if (
          change.analysisPackRetryThreshold !== undefined &&
          this.retry.value === change.analysisPackRetryThreshold
        ) {
          this.retry.setErrors({ server: message('analysisPackRetryThreshold') });
        }
        return of(undefined);
      }),
    );
  }
}
