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
  LabelSettings,
  LabelSettingsUpdate,
  MAX_DOCUMENT_TYPE_PARENT_LENGTH,
  MAX_DOCUMENT_TYPE_PARENT_LEVELS,
  MAX_LABEL_NAME_LENGTH,
  labelPathValidator,
  labelsDifferValidator,
  maxLevelsValidator,
} from './settings.models';
import { SettingsService } from './settings.service';

type LabelField = keyof LabelSettings;
const LABEL_FIELDS: readonly LabelField[] = [
  'actionLabelName',
  'deleteLabelName',
  'documentTypeParent',
];

/** A control that shows one of the group's errors as its own. */
class GroupErrorMatcher implements ErrorStateMatcher {
  constructor(private readonly groupError: string) {}

  isErrorState(control: AbstractControl | null, form: FormGroupDirective | NgForm | null): boolean {
    const invalid = !!control?.invalid || !!control?.parent?.hasError(this.groupError);
    return invalid && !!(control?.touched || control?.dirty || form?.submitted);
  }
}

/**
 * The Settings page's "Labels" section (#202, #244): the action and delete label names and the
 * optional document-type parent. Saves itself with only the changed names; the page passes the
 * loaded names in.
 */
@Component({
  selector: 'app-labels-settings',
  imports: [ReactiveFormsModule, MatButtonModule, MatFormFieldModule, MatInputModule],
  templateUrl: './labels-settings.component.html',
  styles: `
    .help {
      color: var(--mat-sys-on-surface-variant);
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
  readonly maxParentLength = MAX_DOCUMENT_TYPE_PARENT_LENGTH;
  readonly maxParentLevels = MAX_DOCUMENT_TYPE_PARENT_LEVELS;
  readonly deleteMatcher = new GroupErrorMatcher('labelsMatch');
  readonly parentMatcher = new GroupErrorMatcher('parentClash');
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
      documentTypeParent: new FormControl('', {
        nonNullable: true,
        validators: [
          Validators.maxLength(MAX_DOCUMENT_TYPE_PARENT_LENGTH),
          maxLevelsValidator,
          labelPathValidator,
        ],
      }),
    },
    { validators: labelsDifferValidator },
  );
  readonly actionLabelName = this.form.controls.actionLabelName;
  readonly deleteLabelName = this.form.controls.deleteLabelName;
  readonly documentTypeParent = this.form.controls.documentTypeParent;
  readonly saving = signal(false);

  constructor() {
    this.form.disable();
    effect(() => {
      const settings = this.saved();
      untracked(() => this.load(settings));
    });
  }

  /** The trimmed names that differ from the saved ones; a cleared parent is sent as `""`. */
  changes(): LabelSettingsUpdate {
    const saved = this.saved();
    const changes: LabelSettingsUpdate = {};
    for (const field of LABEL_FIELDS) {
      const value = this.form.controls[field].value.trim();
      if (value !== (saved?.[field] ?? '')) changes[field] = value;
    }
    return changes;
  }

  save(): void {
    if (this.saving() || !this.saved()) return;
    // Drops last save's server errors: a parent clash may have been fixed by editing another name.
    for (const control of Object.values(this.form.controls)) control.updateValueAndValidity();
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
    this.settingsApi
      .updateLabels(changes)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (settings) => {
          this.saving.set(false);
          this.saved.set({
            actionLabelName: settings.actionLabelName,
            deleteLabelName: settings.deleteLabelName,
            documentTypeParent: settings.documentTypeParent ?? null,
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
      documentTypeParent: settings.documentTypeParent ?? '',
    });
  }

  // A clash with the saved parent comes back under its field even when only another name changed.
  private applyServerErrors(errors: Record<string, string[]> | null): void {
    if (!errors) return;
    for (const [field, messages] of Object.entries(errors)) {
      const message = messages[0];
      if (!message || !(LABEL_FIELDS as readonly string[]).includes(field)) continue;
      const control = this.form.controls[field as LabelField];
      control.setErrors({ server: message });
      control.markAsTouched();
    }
  }
}

/** ValidationProblem `errors` from a 400, keyed by API field name. */
function validationErrors(error: unknown): Record<string, string[]> | null {
  if (!(error instanceof HttpErrorResponse) || error.status !== 400) return null;
  return (error.error as { errors?: Record<string, string[]> } | null)?.errors ?? null;
}
