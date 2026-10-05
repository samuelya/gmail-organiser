import { HttpErrorResponse } from '@angular/common/http';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  inject,
  Injectable,
  signal,
} from '@angular/core';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import {
  FormArray,
  FormControl,
  FormGroup,
  ReactiveFormsModule,
  ValidationErrors,
} from '@angular/forms';
import { MatAutocompleteModule } from '@angular/material/autocomplete';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import {
  MAT_DIALOG_DATA,
  MatDialog,
  MatDialogModule,
  MatDialogRef,
} from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatTooltipModule } from '@angular/material/tooltip';
import { catchError, concatMap, from, map, Observable, of } from 'rxjs';
import { errorMessage } from '../core/error.interceptor';
import { SettingsService } from '../settings/settings.service';
import { LabelDto } from './labels.models';
import {
  labelPathError,
  labelPathValidator,
  labelPlacement,
  LabelsService,
} from './labels.service';
import { LabelTreePicker } from './label-tree-picker.component';
import { commonMailType, MAIL_TYPES, mailTypeLabel } from './mail-type-chip.component';
import {
  currentLabelsOf,
  decidedReplaceLabels,
  DEFAULT_FLAG_LABELS,
  documentTypeLevels,
  documentTypeOptions,
  editableMembers,
  editRequest,
  normaliseDocumentType,
  ReviewEditDialog,
  ReviewGroupDto,
  replaceState,
  ReviewOutcome,
  SuggestionDto,
  toDocumentTypeLabel,
} from './review.models';
import { ReviewService } from './review.service';

export interface EditSuggestionDialogData {
  /** The outcome the dialog starts from: the member's, or the group card's. */
  current: ReviewOutcome;
  /** The suggestions Save changes, one `PUT` each. */
  members: SuggestionDto[];
  /** A group edit; `truncated` when the card does not list every member. */
  group: { display: string; truncated: boolean } | null;
  /** The members' current labels (their union for a group), one "Replace" checkbox each. */
  currentLabels: string[];
  /** The document-type label the dialog starts from: the member's, or the group card's. */
  documentTypeLabel: string | null;
}

/** A member whose save failed, with the server's reason. */
export interface EditFailure {
  id: string;
  name: string;
  message: string;
}

/** Groups larger than this show a progress bar while saving. */
export const EDIT_PROGRESS_THRESHOLD = 20;

/** Edits the label and flags of one suggestion or of every listed member of a group. */
@Component({
  selector: 'app-edit-suggestion-dialog',
  imports: [
    LabelTreePicker,
    MatAutocompleteModule,
    MatButtonModule,
    MatCheckboxModule,
    MatDialogModule,
    MatFormFieldModule,
    MatInputModule,
    MatProgressBarModule,
    MatSelectModule,
    MatSlideToggleModule,
    MatTooltipModule,
    ReactiveFormsModule,
  ],
  templateUrl: './edit-suggestion-dialog.component.html',
  styles: `
    .muted {
      color: var(--mat-sys-on-surface-variant);
    }
    .error {
      color: var(--mat-sys-error);
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class EditSuggestionDialog {
  readonly data = inject<EditSuggestionDialogData>(MAT_DIALOG_DATA);
  private readonly ref = inject<MatDialogRef<EditSuggestionDialog, boolean>>(MatDialogRef);
  private readonly review = inject(ReviewService);
  private readonly labelsApi = inject(LabelsService);
  private readonly destroyRef = inject(DestroyRef);

  readonly flagLabels = signal(DEFAULT_FLAG_LABELS);
  /** The document-type parent from settings; null hides the field and nothing is sent. */
  readonly documentTypeParent = signal<string | null>(null);
  readonly mailTypeOptions = MAIL_TYPES;
  readonly mailTypeLabel = mailTypeLabel;
  /** The members' shared mail type (`""` for none); `mixed` when they differ. */
  readonly startMailType = startMailType(this.data.members);

  readonly form = new FormGroup({
    topicLabel: new FormControl(this.data.current.topicLabel, {
      nonNullable: true,
      validators: [labelPathValidator],
    }),
    needsAction: new FormControl(this.data.current.needsAction, { nonNullable: true }),
    toBeDeleted: new FormControl(this.data.current.toBeDeleted && !this.allProtected(), {
      nonNullable: true,
    }),
    /** The path under `documentTypeParent`; blank is none. */
    documentType: new FormControl('', {
      nonNullable: true,
      validators: [(c) => this.documentTypeValidator(String(c.value ?? ''))],
    }),
    /** Null while the members' types differ and none is picked. */
    mailType: new FormControl<string | null>(this.startMailType.value),
    /** One per `data.currentLabels`, checked = every member carrying it replaces it. */
    replace: new FormArray(
      this.data.currentLabels.map(
        (l) => new FormControl(replaceState(this.data.members, l) === true, { nonNullable: true }),
      ),
    ),
  });
  /** Indexes of labels only some members replace and the user has not ticked or unticked yet. */
  readonly mixedReplace = signal<ReadonlySet<number>>(
    new Set(
      this.data.currentLabels.flatMap((l, i) =>
        replaceState(this.data.members, l) === null ? [i] : [],
      ),
    ),
  );

  readonly labels = signal<LabelDto[] | null>(null);
  readonly labelsFailed = signal(false);
  readonly path = toSignal(this.form.controls.topicLabel.valueChanges, {
    initialValue: this.form.controls.topicLabel.value,
  });
  readonly placement = computed(() => {
    const labels = this.labels();
    return labels ? labelPlacement(this.path(), labels) : null;
  });
  readonly documentType = toSignal(this.form.controls.documentType.valueChanges, {
    initialValue: '',
  });
  /** The parent's direct children matching the typed text. */
  readonly documentTypeChoices = computed(() => {
    const parent = this.documentTypeParent();
    const labels = this.labels();
    if (!parent || !labels) return [];
    const text = normaliseDocumentType(this.documentType()).toLowerCase();
    return documentTypeOptions(labels, parent).filter((t) => t.toLowerCase().includes(text));
  });
  readonly documentTypeHint = computed(() => {
    const parent = this.documentTypeParent();
    const text = normaliseDocumentType(this.documentType());
    if (!parent || !text) return 'None: no document-type label.';
    const labels = this.labels();
    const exists = labels
      ? documentTypeOptions(labels, parent).some((t) => t.toLowerCase() === text.toLowerCase())
      : false;
    return exists ? `Existing label under ${parent}` : `New: ${text} under ${parent}`;
  });

  readonly protectedCount = this.data.members.filter((m) => m.protected).length;
  readonly total = this.data.members.length;
  readonly saving = signal(false);
  readonly done = signal(0);
  readonly showProgress = this.total > EDIT_PROGRESS_THRESHOLD;
  readonly error = signal<string | null>(null);
  readonly failures = signal<EditFailure[]>([]);
  /** Members already saved; a retry skips them. */
  private readonly saved = new Set<string>();

  constructor() {
    if (this.allProtected()) this.form.controls.toBeDeleted.disable();
    this.loadLabels(this.labelsApi.labels());
    inject(SettingsService)
      .getSettings()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (s) => {
          this.flagLabels.set({ action: s.actionLabelName, delete: s.deleteLabelName });
          this.startDocumentType(s.documentTypeParent ?? null);
        },
        // The error interceptor shows why; the dialog keeps the default flag names and no document type.
        error: () => undefined,
      });
  }

  allProtected(): boolean {
    return this.data.members.length > 0 && this.data.members.every((m) => m.protected);
  }

  pathError(): string | null {
    const errors = this.form.controls.topicLabel.errors;
    return (errors?.['labelPath'] ?? errors?.['server'] ?? null) as string | null;
  }

  documentTypeError(): string | null {
    const errors = this.form.controls.documentType.errors;
    return (errors?.['labelPath'] ?? errors?.['server'] ?? null) as string | null;
  }

  pickLabel(path: string): void {
    this.form.controls.topicLabel.setValue(path);
    this.form.controls.topicLabel.markAsTouched();
  }

  refreshLabels(): void {
    this.loadLabels(this.labelsApi.refresh());
  }

  /**
   * `PUT` per unsaved member in order. A member that fails is reported and the rest still save;
   * the dialog closes when all are saved and otherwise stays open so Save retries the failed ones.
   */
  save(): void {
    if (this.saving()) return;
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    const { topicLabel, needsAction, toBeDeleted } = this.form.getRawValue();
    const outcome = { topicLabel, needsAction, toBeDeleted };
    const decisions = this.replaceDecisions();
    const documentType = this.documentTypeChange();
    const mailType = this.mailTypeChange();
    const failures: EditFailure[] = [];
    let labelError: string | null = null;
    let typeError: string | null = null;
    let mailTypeError: string | null = null;
    this.setSaving(true);
    this.error.set(null);
    this.failures.set([]);
    from(this.data.members.filter((m) => !this.saved.has(m.id)))
      .pipe(
        concatMap((m) =>
          this.review
            .edit(
              m.id,
              editRequest(m, outcome, decidedReplaceLabels(m, decisions), documentType, mailType),
            )
            .pipe(
              map(() => ({ member: m, err: null as unknown })),
              catchError((err: unknown) => of({ member: m, err })),
            ),
        ),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe({
        next: ({ member, err }) => {
          if (err === null) {
            this.saved.add(member.id);
            this.done.set(this.saved.size);
            return;
          }
          const fields = err instanceof HttpErrorResponse ? fieldErrors(err) : {};
          const fieldError = fields['topicLabel']?.[0];
          const documentTypeError = fields['documentTypeLabel']?.[0];
          labelError ??= fieldError ?? null;
          typeError ??= documentTypeError ?? null;
          mailTypeError ??= fields['mailType']?.[0] ?? null;
          failures.push({
            id: member.id,
            name: member.subject || '(no subject)',
            message:
              fieldError ??
              documentTypeError ??
              fields['mailType']?.[0] ??
              fields['replaceLabels']?.[0] ??
              (err instanceof HttpErrorResponse ? errorMessage(err) : 'Saving failed.'),
          });
        },
        complete: () =>
          failures.length
            ? this.failed(failures, labelError, typeError, mailTypeError)
            : this.ref.close(true),
      });
  }

  /** A ticked or unticked label stops being mixed. */
  decideReplace(index: number): void {
    this.mixedReplace.update((mixed) => new Set([...mixed].filter((i) => i !== index)));
  }

  /** Label → replace for every label not left mixed; mixed ones keep each member's own choice. */
  private replaceDecisions(): Map<string, boolean> {
    const checked = this.form.controls.replace.getRawValue();
    const mixed = this.mixedReplace();
    return new Map(
      this.data.currentLabels.flatMap((l, i) => (mixed.has(i) ? [] : [[l, checked[i]] as const])),
    );
  }

  cancel(): void {
    if (this.saving()) return;
    this.ref.close(this.done() > 0);
  }

  private setSaving(saving: boolean): void {
    this.saving.set(saving);
    // Escape and the backdrop would drop the remaining saves.
    this.ref.disableClose = saving;
  }

  private failed(
    failures: EditFailure[],
    labelError: string | null,
    typeError: string | null,
    mailTypeError: string | null,
  ): void {
    this.setSaving(false);
    if (labelError) {
      this.form.controls.topicLabel.setErrors({ server: labelError });
      this.form.controls.topicLabel.markAsTouched();
    }
    if (typeError) {
      this.form.controls.documentType.setErrors({ server: typeError });
      this.form.controls.documentType.markAsTouched();
    }
    if (mailTypeError) {
      this.form.controls.mailType.setErrors({ server: mailTypeError });
      this.form.controls.mailType.markAsTouched();
    }
    // A single edit rejected for its label, document type or mail type only needs the inline field error.
    if (this.total === 1 && (labelError || typeError || mailTypeError)) return;
    this.failures.set(this.total > 1 ? failures : []);
    const saved = this.done() ? ` ${this.done()} of ${this.total} were saved.` : '';
    this.error.set(
      this.total > 1
        ? `${failures.length} of ${this.total} could not be saved.${saved}`
        : failures[0].message,
    );
  }

  /** Shows the field once the parent is known, starting from the current type when it is under the parent. */
  private startDocumentType(parent: string | null): void {
    this.documentTypeParent.set(parent);
    const control = this.form.controls.documentType;
    const prefix = `${parent}/`;
    const current = this.data.documentTypeLabel;
    if (parent && !control.dirty && current?.toLowerCase().startsWith(prefix.toLowerCase())) {
      control.setValue(current.slice(prefix.length));
    }
    control.updateValueAndValidity();
  }

  /** The topic field's path rules for `<parent>/<text>`, within `documentTypeMaxDepth` levels. */
  private documentTypeValidator(text: string): ValidationErrors | null {
    const parent = this.documentTypeParent();
    if (!parent || !text.trim()) return null;
    const label = toDocumentTypeLabel(parent, text);
    const error =
      label === null ? `${documentTypeLevels(parent)} under ${parent}.` : labelPathError(label);
    return error ? { labelPath: error } : null;
  }

  /** The document-type label to send: only when the user changed it (`""` for none). */
  private documentTypeChange(): string | undefined {
    const parent = this.documentTypeParent();
    const control = this.form.controls.documentType;
    if (!parent || !control.dirty) return undefined;
    const label = toDocumentTypeLabel(parent, control.value);
    return label === null || label === (this.data.documentTypeLabel ?? '') ? undefined : label;
  }

  /** The mail type to send: only when the user picked one other than the start (`""` for none). */
  private mailTypeChange(): string | undefined {
    const value = this.form.controls.mailType.value;
    const start = this.startMailType;
    if (value === null || (!start.mixed && value === start.value)) return undefined;
    return value;
  }

  private loadLabels(source: Observable<LabelDto[]>): void {
    this.labelsFailed.set(false);
    source.pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (labels) => this.labels.set(labels),
      // The error interceptor shows why; the label field still works without the tree.
      error: () => this.labelsFailed.set(true),
    });
  }
}

/** The members' shared mail type (`""` when none has one); null and `mixed` when they differ. */
export function startMailType(members: readonly SuggestionDto[]): {
  value: string | null;
  mixed: boolean;
} {
  const common = commonMailType(members);
  const mixed = common === null && members.some((m) => !!m.mailType);
  return { value: mixed ? null : (common ?? ''), mixed };
}

/** The `errors` of an RFC 9457 validation problem. */
export function fieldErrors(error: HttpErrorResponse): Record<string, string[]> {
  const body = error.error as { errors?: unknown } | null;
  return error.status === 400 && body && typeof body.errors === 'object' && body.errors
    ? (body.errors as Record<string, string[]>)
    : {};
}

/** The review page's edit dialog. */
@Injectable()
export class MatReviewEditDialog implements ReviewEditDialog {
  private readonly dialog = inject(MatDialog);

  editGroup(_senderAddress: string, group: ReviewGroupDto): Observable<boolean> {
    return this.open({
      current: {
        topicLabel: group.topicLabel,
        needsAction: group.needsAction,
        toBeDeleted: group.toBeDeleted,
      },
      members: editableMembers(group.members),
      group: { display: group.display, truncated: group.truncated },
      currentLabels: currentLabelsOf(editableMembers(group.members)),
      documentTypeLabel: group.documentTypeLabel,
    });
  }

  editMember(suggestion: SuggestionDto): Observable<boolean> {
    return this.open({
      current: {
        topicLabel: suggestion.topicLabel,
        needsAction: suggestion.needsAction,
        toBeDeleted: suggestion.toBeDeleted,
      },
      members: [suggestion],
      group: null,
      currentLabels: suggestion.currentLabels,
      documentTypeLabel: suggestion.documentTypeLabel,
    });
  }

  private open(data: EditSuggestionDialogData): Observable<boolean> {
    const ref = this.dialog.open<EditSuggestionDialog, EditSuggestionDialogData, boolean>(
      EditSuggestionDialog,
      { data, width: '36rem', maxWidth: '95vw', autoFocus: 'first-tabbable' },
    );
    // Escape after a partial group save still re-fetches.
    const instance = ref.componentInstance;
    return ref.afterClosed().pipe(map((saved) => saved === true || instance.done() > 0));
  }
}
