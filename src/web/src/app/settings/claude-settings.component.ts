import { Clipboard } from '@angular/cdk/clipboard';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
} from '@angular/core';
import { takeUntilDestroyed, toObservable, toSignal } from '@angular/core/rxjs-interop';
import {
  AbstractControl,
  FormControl,
  FormGroup,
  ReactiveFormsModule,
  ValidationErrors,
  ValidatorFn,
  Validators,
} from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatChipsModule } from '@angular/material/chips';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatRadioModule } from '@angular/material/radio';
import { MatSliderModule } from '@angular/material/slider';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatSnackBar } from '@angular/material/snack-bar';
import { catchError, filter, firstValueFrom, of, Subscription, switchMap } from 'rxjs';
import { ClaudeTestResult } from '../core/claude.models';
import { ClaudeService } from '../core/claude.service';
import { openConfirm } from '../core/confirm-dialog';
import {
  CLAUDE_LIMITS,
  CLAUDE_MODES,
  CLAUDE_TOKEN_STATUS,
  ClaudeReviewerMode,
  ClaudeSettings,
  ClaudeSettingsUpdate,
  NumericClaudeField,
  Range,
} from './settings.models';

type NumberControl = FormControl<number | null>;
type LimitField = Exclude<NumericClaudeField, 'claudeSuggestThreshold'>;

interface NumberField {
  key: LimitField;
  label: string;
  hint: string;
}

/** The "Test connection" state; `error` is the API's text, shown verbatim. */
export type ClaudeTestRun =
  | { state: 'idle' }
  | { state: 'running' }
  | { state: 'done'; result: ClaudeTestResult }
  | { state: 'failed' };

const HEADLESS_FIELDS = [
  'claudeRunTimeoutSeconds',
  'claudeMaxItemsPerRun',
  'claudeMaxTurns',
  'claudeModel',
] as const;
const SUGGEST_FIELDS = ['claudeSuggestLowConfidence', 'claudeSuggestNewLabels'] as const;

/**
 * The Settings page's "Claude review" section. Settings in, the changed fields out on Save; the page
 * owns the save call. The MCP snippet, token rotation, prompt copy and connection test call
 * {@link ClaudeService} directly. The OAuth token never reaches the web, only whether it is set.
 */
@Component({
  selector: 'app-claude-settings',
  imports: [
    ReactiveFormsModule,
    MatButtonModule,
    MatChipsModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatProgressSpinnerModule,
    MatRadioModule,
    MatSliderModule,
    MatSlideToggleModule,
  ],
  templateUrl: './claude-settings.component.html',
  styles: `
    .help {
      font: var(--mat-sys-body-small);
      color: var(--mat-sys-on-surface-variant);
      margin: 0;
    }
    .warn {
      color: var(--mat-sys-error);
    }
    a {
      color: inherit;
    }
    .snippet {
      font-family: monospace;
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ClaudeSettingsSection {
  private readonly claude = inject(ClaudeService);
  private readonly clipboard = inject(Clipboard);
  private readonly dialog = inject(MatDialog);
  private readonly snackBar = inject(MatSnackBar);
  private readonly destroyRef = inject(DestroyRef);

  /** `null` until loaded; the form stays disabled until then. */
  readonly settings = input<ClaudeSettings | null>(null);
  readonly saving = input(false);
  /** ValidationProblem `errors` from the last save, keyed by API field name. */
  readonly serverErrors = input<Record<string, string[]> | null>(null);

  readonly changed = output<ClaudeSettingsUpdate>();
  /** Asks the page to scroll to another card, by its heading id. */
  readonly jump = output<string>();

  readonly modes = CLAUDE_MODES;
  readonly tokenStatus = CLAUDE_TOKEN_STATUS;
  readonly threshold = CLAUDE_LIMITS.claudeSuggestThreshold;
  readonly numberFields: readonly NumberField[] = [
    {
      key: 'claudeRunTimeoutSeconds',
      label: 'Timeout per run (seconds)',
      hint: 'A run that takes longer stops; its items show "Claude unavailable".',
    },
    {
      key: 'claudeMaxItemsPerRun',
      label: 'Max items per run',
      hint: 'Larger sends are split into runs of this size.',
    },
    {
      key: 'claudeMaxTurns',
      label: 'Max turns per run',
      hint: 'Tool calls Claude may make in one run.',
    },
  ];

  readonly form = new FormGroup({
    claudeReviewerMode: new FormControl<ClaudeReviewerMode>('off', { nonNullable: true }),
    claudeSuggestLowConfidence: new FormControl(false, { nonNullable: true }),
    claudeSuggestThreshold: numberControl('claudeSuggestThreshold'),
    claudeSuggestNewLabels: new FormControl(false, { nonNullable: true }),
    claudeRunTimeoutSeconds: numberControl('claudeRunTimeoutSeconds'),
    claudeMaxItemsPerRun: numberControl('claudeMaxItemsPerRun'),
    claudeMaxTurns: numberControl('claudeMaxTurns'),
    claudeModel: new FormControl('', { nonNullable: true }),
  });
  readonly c = this.form.controls;

  readonly mode = toSignal(this.c.claudeReviewerMode.valueChanges, { initialValue: 'off' });
  readonly headless = computed(() => this.mode() === 'headless_claude_code');
  readonly desktop = computed(() => this.mode() === 'claude_desktop');
  /** The test runs against the saved mode, so it waits for an unsaved mode change. */
  readonly modeSaved = computed(() => this.mode() === this.settings()?.claudeReviewerMode);

  readonly snippet = signal<string | null>(null);
  readonly snippetLoading = signal(false);
  readonly rotating = signal(false);
  readonly copyingPrompt = signal(false);
  readonly testRun = signal<ClaudeTestRun>({ state: 'idle' });
  private testSub: Subscription | null = null;

  constructor() {
    this.form.disable();
    effect(() => {
      const settings = this.settings();
      untracked(() => this.load(settings));
    });
    effect(() => {
      const errors = this.serverErrors();
      untracked(() => this.showServerErrors(errors));
    });
    // The snippet is fetched the first time Claude Desktop is chosen, then kept.
    toObservable(this.desktop)
      .pipe(
        filter((shown) => shown && this.snippet() === null && !this.snippetLoading()),
        switchMap(() => {
          this.snippetLoading.set(true);
          return this.claude.getMcpConfig().pipe(catchError(() => of(null)));
        }),
        takeUntilDestroyed(),
      )
      .subscribe((config) => {
        this.snippetLoading.set(false);
        this.snippet.set(config?.claudeDesktopSnippet ?? null);
      });
    this.c.claudeReviewerMode.valueChanges.pipe(takeUntilDestroyed()).subscribe(() => {
      this.applyMode();
      this.cancelTest();
    });
    this.c.claudeSuggestLowConfidence.valueChanges
      .pipe(takeUntilDestroyed())
      .subscribe(() => this.applyMode());
  }

  save(): void {
    if (this.saving() || !this.settings()) return;
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    this.changed.emit(this.changes());
  }

  /** Puts the form back to the loaded settings. */
  discard(): void {
    this.load(this.settings());
  }

  /** The fields that differ from the loaded settings; hidden (disabled) controls are never sent. */
  changes(): ClaudeSettingsUpdate {
    const saved = this.settings();
    if (!saved) return {};
    const changes: ClaudeSettingsUpdate = {};
    if (this.c.claudeReviewerMode.value !== saved.claudeReviewerMode) {
      changes.claudeReviewerMode = this.c.claudeReviewerMode.value;
    }
    for (const key of SUGGEST_FIELDS) {
      const control = this.c[key];
      if (control.enabled && control.value !== saved[key]) changes[key] = control.value;
    }
    for (const key of [
      'claudeSuggestThreshold',
      ...this.numberFields.map((f) => f.key),
    ] as NumericClaudeField[]) {
      const control = this.c[key];
      if (control.enabled && control.value !== null && control.value !== saved[key]) {
        changes[key] = control.value;
      }
    }
    // The API treats a missing value as "unchanged" and an empty name as "clear".
    const model = this.c.claudeModel;
    if (model.enabled && model.value.trim() !== (saved.claudeModel ?? '')) {
      changes.claudeModel = model.value.trim();
    }
    return changes;
  }

  control(key: LimitField): NumberControl {
    return this.c[key];
  }

  range(key: NumericClaudeField): Range {
    return CLAUDE_LIMITS[key];
  }

  percent(value: number | null): string {
    return value === null ? '' : `${Math.round(value * 100)} %`;
  }

  testConnection(): void {
    if (this.testRun().state === 'running') return;
    this.testRun.set({ state: 'running' });
    this.testSub = this.claude
      .testConnection()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (result) => this.testRun.set({ state: 'done', result }),
        // The error interceptor shows why.
        error: () => this.testRun.set({ state: 'failed' }),
      });
  }

  cancelTest(): void {
    this.testSub?.unsubscribe();
    this.testSub = null;
    this.testRun.set({ state: 'idle' });
  }

  copySnippet(): void {
    const snippet = this.snippet();
    if (snippet && this.clipboard.copy(snippet)) {
      this.snackBar.open('Claude Desktop config copied', undefined, { duration: 2000 });
    }
  }

  rotateToken(): void {
    if (this.rotating()) return;
    openConfirm(this.dialog, {
      title: 'Rotate the MCP token?',
      message:
        'Claude Desktop stops connecting until you paste the new config into claude_desktop_config.json.',
      confirm: 'Rotate',
    })
      .pipe(
        filter(Boolean),
        switchMap(() => {
          this.rotating.set(true);
          return this.claude.rotateMcpToken();
        }),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe({
        next: (config) => {
          this.rotating.set(false);
          this.snippet.set(config.claudeDesktopSnippet);
          this.snackBar.open('MCP token rotated; update Claude Desktop', undefined, {
            duration: 3000,
          });
        },
        error: () => this.rotating.set(false),
      });
  }

  copyPrompt(): void {
    if (this.copyingPrompt()) return;
    this.copyingPrompt.set(true);
    let loaded = false;
    const prompt = firstValueFrom(
      this.claude.getReviewPrompt().pipe(takeUntilDestroyed(this.destroyRef)),
    ).then((text) => {
      loaded = true;
      return text;
    });
    this.writePrompt(prompt)
      .then(
        () => this.snackBar.open('Review prompt copied', undefined, { duration: 2000 }),
        () => {
          // A failed request is shown by the error interceptor; only a failed copy is ours.
          if (loaded) this.snackBar.open('Could not copy the review prompt', 'Close');
        },
      )
      .finally(() => this.copyingPrompt.set(false));
  }

  /**
   * Starts the clipboard write inside the click, with the prompt still loading, so the browser
   * keeps the user gesture; copying after the response arrives fails in Safari and Firefox.
   */
  private writePrompt(prompt: Promise<string>): Promise<void> {
    if (typeof ClipboardItem !== 'undefined' && navigator.clipboard?.write) {
      const blob = prompt.then((text) => new Blob([text], { type: 'text/plain' }));
      return navigator.clipboard.write([new ClipboardItem({ 'text/plain': blob })]);
    }
    return prompt.then((text) => {
      if (!this.clipboard.copy(text)) throw new Error('copy failed');
    });
  }

  private load(settings: ClaudeSettings | null): void {
    if (!settings) {
      this.form.disable();
      return;
    }
    this.form.enable({ emitEvent: false });
    this.form.reset({
      claudeReviewerMode: settings.claudeReviewerMode,
      claudeSuggestLowConfidence: settings.claudeSuggestLowConfidence,
      claudeSuggestThreshold: settings.claudeSuggestThreshold,
      claudeSuggestNewLabels: settings.claudeSuggestNewLabels,
      claudeRunTimeoutSeconds: settings.claudeRunTimeoutSeconds,
      claudeMaxItemsPerRun: settings.claudeMaxItemsPerRun,
      claudeMaxTurns: settings.claudeMaxTurns,
      claudeModel: settings.claudeModel ?? '',
    });
    this.applyMode();
  }

  /** Disables the controls the chosen mode hides, so they are neither validated nor sent. */
  private applyMode(): void {
    if (!this.settings()) return;
    const mode = this.c.claudeReviewerMode.value;
    for (const key of HEADLESS_FIELDS) {
      setEnabled(this.c[key], mode === 'headless_claude_code');
    }
    for (const key of SUGGEST_FIELDS) setEnabled(this.c[key], mode !== 'off');
    setEnabled(
      this.c.claudeSuggestThreshold,
      mode !== 'off' && this.c.claudeSuggestLowConfidence.value,
    );
  }

  private showServerErrors(errors: Record<string, string[]> | null): void {
    for (const [key, messages] of Object.entries(errors ?? {})) {
      const control = this.form.get(key);
      if (control && messages.length > 0) {
        control.setErrors({ server: messages[0] });
        control.markAsTouched();
      }
    }
  }
}

function setEnabled(control: AbstractControl, enabled: boolean): void {
  if (enabled) control.enable({ emitEvent: false });
  else control.disable({ emitEvent: false });
}

function numberControl(field: NumericClaudeField): NumberControl {
  const { min, max, integer } = CLAUDE_LIMITS[field];
  const validators: ValidatorFn[] = [Validators.required, Validators.min(min), Validators.max(max)];
  if (integer) validators.push(wholeNumber);
  return new FormControl<number | null>(null, { validators });
}

/** `min`/`max` accept 2.5; the API's integer fields would reject it. */
function wholeNumber(control: AbstractControl<number | null>): ValidationErrors | null {
  const value = control.value;
  return value === null || Number.isInteger(value) ? null : { integer: true };
}
