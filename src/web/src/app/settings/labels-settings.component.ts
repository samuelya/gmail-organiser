import { HttpErrorResponse } from '@angular/common/http';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  effect,
  inject,
  input,
  linkedSignal,
  signal,
  untracked,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import {
  AbstractControl,
  FormControl,
  FormGroup,
  FormGroupDirective,
  NgForm,
  ReactiveFormsModule,
  Validators,
} from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { ErrorStateMatcher } from '@angular/material/core';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSnackBar } from '@angular/material/snack-bar';
import {
  DOCUMENT_TYPE_PARENT_FIELD,
  LabelSettings,
  LabelSettingsUpdate,
  MAX_LABEL_NAME_LENGTH,
  labelPathValidator,
  labelsDifferValidator,
} from './settings.models';
import { SettingsService } from './settings.service';

type LabelField = keyof LabelSettings;

/** The delete label shows the group's "must differ" error as its own. */
class DeleteLabelErrorMatcher implements ErrorStateMatcher {
  isErrorState(control: AbstractControl | null, form: FormGroupDirective | NgForm | null): boolean {
    const invalid = !!control?.invalid || !!control?.parent?.hasError('labelsMatch');
    return invalid && !!(control?.touched || control?.dirty || form?.submitted);
  }
}

/**
 * The Settings page's "Labels" section (#202): the action and delete label names. Saves itself with
 * only the changed names; the page passes the loaded names in.
 */
@Component({
  selector: 'app-labels-settings',
  imports: [ReactiveFormsModule, MatButtonModule, MatFormFieldModule, MatInputModule],
  templateUrl: './labels-settings.component.html',
  styles: `
    .help {
      color: var(--mat-sys-on-surface-variant);
    }
    .labels-error {
      color: var(--mat-sys-error);
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LabelsSettingsSection {
  private readonly settingsApi = inject(SettingsService);
  private readonly snackBar = inject(MatSnackBar);
  private readonly destroyRef = inject(DestroyRef);

  /** `null` until loaded; the form stays disabled until then. */
  readonly settings = input<LabelSettings | null>(null);
  /** The names as last saved: the loaded ones, then each save's response. */
  private readonly saved = linkedSignal(() => this.settings());

  readonly maxLength = MAX_LABEL_NAME_LENGTH;
  readonly deleteMatcher = new DeleteLabelErrorMatcher();
  readonly form = new FormGroup(
    {
      actionLabelName: new FormControl('', {
        nonNullable: true,
        validators: [
          Validators.required,
          Validators.maxLength(MAX_LABEL_NAME_LENGTH),
          labelPathValidator,
        ],
      }),
      deleteLabelName: new FormControl('', {
        nonNullable: true,
        validators: [
          Validators.required,
          Validators.maxLength(MAX_LABEL_NAME_LENGTH),
          labelPathValidator,
        ],
      }),
    },
    { validators: labelsDifferValidator },
  );
  readonly actionLabelName = this.form.controls.actionLabelName;
  readonly deleteLabelName = this.form.controls.deleteLabelName;
  readonly saving = signal(false);
  /** A server error with no field here, e.g. a clash with the document-type parent label. */
  readonly formError = signal<string | null>(null);

  constructor() {
    this.form.disable();
    effect(() => {
      const settings = this.saved();
      untracked(() => this.load(settings));
    });
  }

  /** The trimmed names that differ from the saved ones. */
  changes(): LabelSettingsUpdate {
    const saved = this.saved();
    const changes: LabelSettingsUpdate = {};
    for (const field of ['actionLabelName', 'deleteLabelName'] as const) {
      const value = this.form.controls[field].value.trim();
      if (value !== saved?.[field]) changes[field] = value;
    }
    return changes;
  }

  save(): void {
    if (this.saving() || !this.saved()) return;
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    const changes = this.changes();
    if (Object.keys(changes).length === 0) {
      this.snackBar.open('No label changes to save', undefined, { duration: 3000 });
      return;
    }
    this.saving.set(true);
    this.formError.set(null);
    this.settingsApi
      .updateLabels(changes)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (settings) => {
          this.saving.set(false);
          this.saved.set({
            actionLabelName: settings.actionLabelName,
            deleteLabelName: settings.deleteLabelName,
          });
          this.snackBar.open('Label settings saved', undefined, { duration: 3000 });
        },
        // The error interceptor shows why; field errors also land on their controls.
        error: (error: unknown) => {
          this.saving.set(false);
          this.applyServerErrors(validationErrors(error));
        },
      });
  }

  private load(settings: LabelSettings | null): void {
    if (!settings) {
      this.form.disable();
      return;
    }
    this.form.enable();
    this.form.reset({
      actionLabelName: settings.actionLabelName,
      deleteLabelName: settings.deleteLabelName,
    });
  }

  private applyServerErrors(errors: Record<string, string[]> | null): void {
    if (!errors) return;
    for (const [field, messages] of Object.entries(errors)) {
      const message = messages[0];
      if (!message) continue;
      if (field === 'actionLabelName' || field === 'deleteLabelName') {
        const control = this.form.controls[field as LabelField];
        control.setErrors({ server: message });
        control.markAsTouched();
      } else if (field === DOCUMENT_TYPE_PARENT_FIELD) {
        this.formError.set(`Document-type parent label: ${message}`);
      }
    }
  }
}

/** ValidationProblem `errors` from a 400, keyed by API field name. */
function validationErrors(error: unknown): Record<string, string[]> | null {
  if (!(error instanceof HttpErrorResponse) || error.status !== 400) return null;
  return (error.error as { errors?: Record<string, string[]> } | null)?.errors ?? null;
}
