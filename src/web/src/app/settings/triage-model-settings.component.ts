import {
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  inject,
  input,
  OnInit,
  signal,
} from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { FormControl, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSelectModule } from '@angular/material/select';
import { MatSliderModule } from '@angular/material/slider';
import { MatSnackBar } from '@angular/material/snack-bar';
import {
  catchError,
  concatMap,
  debounceTime,
  filter,
  map,
  of,
  Subject,
  Subscription,
  switchMap,
} from 'rxjs';
import { LlmModels, LlmService } from '../core/llm.service';
import { ModelTest, modelOptions, testSeconds } from '../setup/steps/models-step.component';
import { SettingsService } from './settings.service';
import { SettingsDto } from './settings.models';
import {
  percent,
  problemErrors,
  TRIAGE_CONFIDENCE_THRESHOLD,
  TriageModelSettings,
  TriageUpdate,
} from './triage-settings.models';

/** The slider settles this long before it is saved. */
export const TRIAGE_SAVE_DEBOUNCE_MS = 600;

/** Shown on a field whose save failed without a field message; its value stays in the field. */
export const NOT_SAVED = 'Not saved; it is sent again with the next change.';

/**
 * The Settings page's triage model (#380): an optional smaller chat model asked first, with its own
 * "Test model", and the confidence its answers need. Each field is saved as it changes; a failed
 * save keeps the value shown with the reason on the field. Loads and saves itself.
 */
@Component({
  selector: 'app-triage-model-settings',
  imports: [
    ReactiveFormsModule,
    MatButtonModule,
    MatFormFieldModule,
    MatIconModule,
    MatProgressSpinnerModule,
    MatSelectModule,
    MatSliderModule,
  ],
  template: `
    <div class="flex max-w-2xl flex-col gap-4">
      <div class="flex flex-wrap items-start gap-2">
        <mat-form-field class="min-w-0 flex-1 basis-72">
          <mat-label>Triage model (optional)</mat-label>
          <mat-select [formControl]="model" data-testid="triage-model">
            <mat-option [value]="null">None (the chat model answers everything)</mat-option>
            @for (option of options(); track option.value) {
              <mat-option [value]="option.value">{{ option.label }}</mat-option>
            }
          </mat-select>
          @if (model.hasError('server')) {
            <mat-error data-testid="triage-model-error">{{ model.getError('server') }}</mat-error>
          }
          <mat-hint data-testid="triage-model-hint">
            @if (modelsLoading()) {
              Loading the models from Ollama…
            } @else if (!models()?.reachable) {
              {{ models()?.error || 'Ollama is not reachable.' }}
            } @else {
              A smaller model asked first; the chat model answers when it is unsure. The chat model
              itself is the same as none.
            }
          </mat-hint>
        </mat-form-field>
        <div class="flex min-h-14 flex-wrap items-center gap-2" aria-live="polite">
          @let run = testRun();
          @if (run.state === 'running') {
            <mat-spinner diameter="20" aria-label="Testing the triage model" />
            <button
              mat-button
              type="button"
              (click)="cancelTest()"
              data-testid="cancel-test-triage"
            >
              Cancel
            </button>
          } @else {
            <button
              mat-stroked-button
              type="button"
              (click)="test()"
              [disabled]="!model.value"
              aria-label="Test the triage model"
              data-testid="test-triage"
            >
              Test model
            </button>
          }
          @if (run.state === 'ok') {
            <span class="flex items-center gap-1" data-testid="test-ok-triage">
              <mat-icon aria-hidden="true">check_circle</mat-icon>
              Answered in {{ run.seconds }} s
            </span>
          } @else if (run.state === 'error') {
            <span class="flex items-center gap-1" data-testid="test-error-triage">
              <mat-icon aria-hidden="true">error</mat-icon>
              {{ run.error }}
            </span>
          }
        </div>
      </div>

      <fieldset class="flex flex-col gap-1">
        <legend id="triage-threshold-label" class="mb-1">Triage confidence threshold</legend>
        <div class="flex items-center gap-4">
          <mat-slider
            class="grow"
            [min]="range.min"
            [max]="range.max"
            [step]="range.step"
            discrete
            [displayWith]="percent"
          >
            <input
              matSliderThumb
              [formControl]="threshold"
              aria-labelledby="triage-threshold-label"
              data-testid="triage-threshold"
            />
          </mat-slider>
          <span class="w-12 text-right" aria-hidden="true">{{ percent(threshold.value) }}</span>
        </div>
        <p class="help">
          The triage answer is kept when every email in the prompt reaches this confidence;
          otherwise the chat model answers the same prompt.
        </p>
        @if (threshold.hasError('server')) {
          <p class="help warn" role="alert" data-testid="triage-threshold-error">
            {{ threshold.getError('server') }}
          </p>
        }
      </fieldset>
    </div>
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
export class TriageModelSettingsSection implements OnInit {
  private readonly settingsApi = inject(SettingsService);
  private readonly llm = inject(LlmService);
  private readonly snackBar = inject(MatSnackBar);
  private readonly destroyRef = inject(DestroyRef);
  private testRequest?: Subscription;

  /** The Ollama URL to list and test against; `null` uses the saved URL. */
  readonly baseUrl = input<string | null>(null);

  readonly range = TRIAGE_CONFIDENCE_THRESHOLD;
  readonly percent = percent;

  readonly models = signal<LlmModels | null>(null);
  readonly modelsLoading = signal(true);
  /** The saved triage model, listed even when the server no longer has it. */
  private readonly savedModel = signal<string | null>(null);
  readonly options = computed(() =>
    modelOptions(this.models()?.chatModels ?? [], this.savedModel()),
  );
  readonly testRun = signal<ModelTest>({ state: 'idle' });

  readonly model = new FormControl<string | null>(null);
  readonly threshold = new FormControl<number | null>(null, [
    Validators.required,
    Validators.min(TRIAGE_CONFIDENCE_THRESHOLD.min),
    Validators.max(TRIAGE_CONFIDENCE_THRESHOLD.max),
  ]);
  private readonly changes = new Subject<TriageUpdate>();

  constructor() {
    this.model.disable();
    this.threshold.disable();
    this.destroyRef.onDestroy(() => this.testRequest?.unsubscribe());
    toObservable(this.baseUrl)
      .pipe(
        switchMap((url) => {
          this.modelsLoading.set(true);
          return this.llm.getModels(url).pipe(catchError(() => of(null)));
        }),
        takeUntilDestroyed(),
      )
      .subscribe((models) => {
        this.modelsLoading.set(false);
        this.models.set(models);
      });
    this.model.valueChanges.pipe(takeUntilDestroyed()).subscribe((value) => {
      // A result belongs to the model it was run for.
      this.cancelTest();
      // The API treats a missing value as "unchanged" and an empty name as "clear", so None is ''.
      this.changes.next({ triageModel: value ?? '' });
    });
    this.threshold.valueChanges
      .pipe(
        debounceTime(TRIAGE_SAVE_DEBOUNCE_MS),
        filter(() => this.threshold.valid),
        takeUntilDestroyed(),
      )
      .subscribe((value) => this.changes.next({ triageConfidenceThreshold: value! }));
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

  /** Runs in the background: the fields stay usable, and Cancel aborts the request. */
  test(): void {
    const model = this.model.value;
    if (!model) return;
    this.testRequest?.unsubscribe();
    this.testRun.set({ state: 'running' });
    this.testRequest = this.llm.testModel('chat', model, this.baseUrl()).subscribe({
      next: (result) =>
        this.testRun.set(
          result.ok
            ? { state: 'ok', seconds: testSeconds(result.elapsedMs) }
            : { state: 'error', error: result.error ?? 'The model test failed.' },
        ),
      error: () => this.testRun.set({ state: 'error', error: 'The model test request failed.' }),
    });
  }

  cancelTest(): void {
    this.testRequest?.unsubscribe();
    this.testRequest = undefined;
    this.testRun.set({ state: 'idle' });
  }

  /** An older API without the fields leaves the section disabled. */
  private load(settings: SettingsDto & Partial<TriageModelSettings>): void {
    if (settings.triageConfidenceThreshold === undefined) return;
    this.savedModel.set(settings.triageModel ?? null);
    this.model.setValue(settings.triageModel ?? null, { emitEvent: false });
    this.threshold.setValue(settings.triageConfidenceThreshold, { emitEvent: false });
    this.model.enable({ emitEvent: false });
    this.threshold.enable({ emitEvent: false });
  }

  private save(change: TriageUpdate) {
    return this.settingsApi.saveTriage(change).pipe(
      map((settings) => {
        if (change.triageModel !== undefined) this.savedModel.set(settings.triageModel ?? null);
        this.snackBar.open('Triage settings saved', undefined, { duration: 3000 });
      }),
      // The value stays in its field, marked as not saved; the next change sends it again.
      catchError((error: unknown) => {
        const errors = problemErrors(error);
        const message = (key: string) => errors[key]?.[0] ?? errors['']?.[0] ?? NOT_SAVED;
        if (change.triageModel !== undefined && (this.model.value ?? '') === change.triageModel) {
          this.model.setErrors({ server: message('triageModel') });
          this.model.markAsTouched();
        }
        if (
          change.triageConfidenceThreshold !== undefined &&
          this.threshold.value === change.triageConfidenceThreshold
        ) {
          this.threshold.setErrors({ server: message('triageConfidenceThreshold') });
        }
        return of(undefined);
      }),
    );
  }
}
