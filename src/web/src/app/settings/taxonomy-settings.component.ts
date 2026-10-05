import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  inject,
  OnInit,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import {
  AbstractControl,
  FormControl,
  ReactiveFormsModule,
  ValidationErrors,
  Validators,
} from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatChipsModule } from '@angular/material/chips';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatSnackBar } from '@angular/material/snack-bar';
import { catchError, concatMap, debounceTime, filter, map, of, Subject } from 'rxjs';
import { SettingsService } from './settings.service';
import {
  blockedLabelError,
  MAX_BLOCKED_LABELS,
  MAX_NEW_LABELS_PER_RUN,
  problemErrors,
  TaxonomySettings,
  TaxonomyUpdate,
  wholeNumber,
} from './triage-settings.models';

/** The number field settles this long before it is saved. */
export const TAXONOMY_SAVE_DEBOUNCE_MS = 600;

/**
 * The Settings page's "Taxonomy" section (#371): the lock to the approved label set, the cap on new
 * labels per run and the blocked labels, each saved as it changes. Loads and saves itself.
 */
@Component({
  selector: 'app-taxonomy-settings',
  imports: [
    ReactiveFormsModule,
    MatButtonModule,
    MatChipsModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatSlideToggleModule,
  ],
  template: `
    <div class="flex max-w-2xl flex-col gap-4">
      <div class="flex flex-col gap-1">
        <mat-slide-toggle
          [formControl]="locked"
          aria-describedby="taxonomy-locked-help"
          data-testid="taxonomy-locked"
        >
          Lock labels to the approved taxonomy
        </mat-slide-toggle>
        <p class="help ml-14" id="taxonomy-locked-help">
          Analysis picks topic labels from your label tree only. A new label is a proposal you
          approve one by one; it is never bulk-approved.
        </p>
      </div>

      <mat-form-field subscriptSizing="dynamic">
        <mat-label>Max new labels per run</mat-label>
        <input
          matInput
          type="number"
          [formControl]="maxNew"
          [min]="maxNewRange.min"
          [max]="maxNewRange.max"
          [step]="maxNewRange.step"
          data-testid="taxonomy-max-new"
        />
        @if (maxNew.hasError('server')) {
          <mat-error data-testid="taxonomy-max-new-error">{{
            maxNew.getError('server')
          }}</mat-error>
        } @else if (maxNew.invalid) {
          <mat-error data-testid="taxonomy-max-new-error"
            >Enter a whole number from {{ maxNewRange.min }} to {{ maxNewRange.max }}.</mat-error
          >
        }
        <mat-hint>New labels past this many in one run lose their confidence.</mat-hint>
      </mat-form-field>

      <section class="flex flex-col gap-2" aria-labelledby="blocked-labels-title">
        <h3 id="blocked-labels-title" class="m-0">Blocked labels</h3>
        <p class="help">Names the analysis never uses, at any level of a label path.</p>
        @if (blocked().length) {
          <mat-chip-set aria-label="Blocked labels">
            @for (label of blocked(); track label) {
              <mat-chip data-testid="blocked-chip" (removed)="removeBlocked(label)">
                {{ label }}
                <button
                  matChipRemove
                  [attr.aria-label]="'Unblock ' + label"
                  data-testid="blocked-remove"
                >
                  <mat-icon aria-hidden="true">cancel</mat-icon>
                </button>
              </mat-chip>
            }
          </mat-chip-set>
        }
        @if (blockedError(); as error) {
          <p class="error m-0" role="alert" data-testid="blocked-server-error">{{ error }}</p>
        }
        <form class="flex flex-wrap items-start gap-2" (ngSubmit)="addBlocked()" novalidate>
          <mat-form-field class="min-w-48 flex-1" subscriptSizing="dynamic">
            <mat-label>Block a label</mat-label>
            <input
              matInput
              autocomplete="off"
              [formControl]="newBlocked"
              data-testid="blocked-new"
            />
            @if (newBlocked.hasError('duplicate')) {
              <mat-error data-testid="blocked-new-error">That label is already blocked.</mat-error>
            } @else if (newBlocked.hasError('blocked')) {
              <mat-error data-testid="blocked-new-error">{{
                newBlocked.getError('blocked')
              }}</mat-error>
            }
          </mat-form-field>
          <button
            mat-stroked-button
            type="submit"
            class="mt-2"
            [disabled]="!loaded() || blocked().length >= maxBlocked"
            data-testid="blocked-add"
          >
            Block
          </button>
        </form>
      </section>
    </div>
  `,
  styles: `
    .help {
      font: var(--mat-sys-body-small);
      color: var(--mat-sys-on-surface-variant);
      margin: 0;
    }
    .error {
      font: var(--mat-sys-body-small);
      color: var(--mat-sys-error);
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TaxonomySettingsSection implements OnInit {
  private readonly settingsApi = inject(SettingsService);
  private readonly snackBar = inject(MatSnackBar);
  private readonly destroyRef = inject(DestroyRef);

  readonly maxNewRange = MAX_NEW_LABELS_PER_RUN;
  readonly maxBlocked = MAX_BLOCKED_LABELS;

  readonly loaded = signal(false);
  readonly locked = new FormControl(false, { nonNullable: true });
  readonly maxNew = new FormControl<number | null>(null, [
    Validators.required,
    Validators.min(MAX_NEW_LABELS_PER_RUN.min),
    Validators.max(MAX_NEW_LABELS_PER_RUN.max),
    wholeNumber,
  ]);
  readonly blocked = signal<readonly string[]>([]);
  readonly blockedError = signal<string | null>(null);
  readonly newBlocked = new FormControl('', {
    nonNullable: true,
    validators: [blockedValidator, (c: AbstractControl<string>) => this.unique(c)],
  });
  /** The fields as last saved; a failed save goes back to them. */
  private saved: TaxonomySettings | null = null;
  private readonly changes = new Subject<TaxonomyUpdate>();

  constructor() {
    this.locked.disable();
    this.maxNew.disable();
    this.locked.valueChanges
      .pipe(takeUntilDestroyed())
      .subscribe((taxonomyLocked) => this.changes.next({ taxonomyLocked }));
    this.maxNew.valueChanges
      .pipe(
        debounceTime(TAXONOMY_SAVE_DEBOUNCE_MS),
        filter(() => this.maxNew.valid),
        takeUntilDestroyed(),
      )
      .subscribe((value) => this.changes.next({ analysisMaxNewLabelsPerRun: value! }));
    this.changes
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

  addBlocked(): void {
    this.newBlocked.updateValueAndValidity();
    const name = this.newBlocked.value.trim();
    if (
      !name ||
      this.newBlocked.invalid ||
      !this.loaded() ||
      this.blocked().length >= this.maxBlocked
    ) {
      this.newBlocked.markAsTouched();
      return;
    }
    this.blocked.update((list) => [...list, name]);
    this.newBlocked.reset();
    this.changes.next({ analysisBlockedLabels: [...this.blocked()] });
  }

  removeBlocked(label: string): void {
    this.blocked.update((list) => list.filter((l) => l !== label));
    this.newBlocked.updateValueAndValidity();
    this.changes.next({ analysisBlockedLabels: [...this.blocked()] });
  }

  private load(settings: TaxonomySettings): void {
    this.saved = {
      taxonomyLocked: settings.taxonomyLocked,
      analysisMaxNewLabelsPerRun: settings.analysisMaxNewLabelsPerRun,
      analysisBlockedLabels: [...(settings.analysisBlockedLabels ?? [])],
    };
    this.locked.setValue(this.saved.taxonomyLocked, { emitEvent: false });
    this.maxNew.setValue(this.saved.analysisMaxNewLabelsPerRun, { emitEvent: false });
    this.blocked.set(this.saved.analysisBlockedLabels);
    this.locked.enable({ emitEvent: false });
    this.maxNew.enable({ emitEvent: false });
    this.loaded.set(true);
  }

  private save(change: TaxonomyUpdate) {
    return this.settingsApi.saveTaxonomy(change).pipe(
      map((settings) => {
        // Only the fields sent: a later change may already be queued with its own value.
        if (this.saved) this.saved = { ...this.saved, ...pick(settings, change) };
        if (change.analysisBlockedLabels) this.blockedError.set(null);
        this.snackBar.open('Taxonomy settings saved', undefined, { duration: 3000 });
      }),
      // A 400 is shown on its field; the error interceptor shows anything else.
      catchError((error: unknown) => {
        const errors = problemErrors(error);
        const saved = this.saved;
        if (change.taxonomyLocked !== undefined && saved)
          this.locked.setValue(saved.taxonomyLocked, { emitEvent: false });
        if (errors['analysisMaxNewLabelsPerRun']) {
          this.maxNew.setErrors({ server: errors['analysisMaxNewLabelsPerRun'][0] });
          this.maxNew.markAsTouched();
        }
        if (change.analysisBlockedLabels && saved) {
          this.blocked.set(saved.analysisBlockedLabels);
          this.blockedError.set(errors['analysisBlockedLabels']?.[0] ?? null);
        }
        return of(undefined);
      }),
    );
  }

  /** `{ duplicate: true }` when the name is blocked already (case-insensitive). */
  private unique(control: AbstractControl<string>): ValidationErrors | null {
    const name = (control.value ?? '').trim().toLowerCase();
    return name && this.blocked?.().some((l) => l.toLowerCase() === name)
      ? { duplicate: true }
      : null;
  }
}

/** `blockedLabelError` as a validator: `{ blocked: message }`; blank waits for input. */
function blockedValidator(control: AbstractControl<string>): ValidationErrors | null {
  if (!(control.value ?? '').trim()) return null;
  const error = blockedLabelError(control.value);
  return error ? { blocked: error } : null;
}

/** The saved values of the fields `change` sent. */
function pick(settings: TaxonomySettings, change: TaxonomyUpdate): TaxonomyUpdate {
  return Object.fromEntries(
    Object.keys(change).map((k) => [k, settings[k as keyof TaxonomySettings]]),
  ) as TaxonomyUpdate;
}
