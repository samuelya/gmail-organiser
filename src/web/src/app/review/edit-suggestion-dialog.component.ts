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
import { FormControl, FormGroup, ReactiveFormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import {
  MAT_DIALOG_DATA,
  MatDialog,
  MatDialogModule,
  MatDialogRef,
} from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatTooltipModule } from '@angular/material/tooltip';
import { catchError, concatMap, from, map, Observable, of } from 'rxjs';
import { errorMessage } from '../core/error.interceptor';
import { SettingsService } from '../settings/settings.service';
import { LabelDto } from './labels.models';
import { labelPathValidator, labelPlacement, LabelsService } from './labels.service';
import { LabelTreePicker } from './label-tree-picker.component';
import {
  DEFAULT_FLAG_LABELS,
  editableMembers,
  editRequest,
  ReviewEditDialog,
  ReviewGroupDto,
  ReviewOutcome,
  SuggestionDto,
} from './review.models';
import { ReviewService } from './review.service';

export interface EditSuggestionDialogData {
  /** The outcome the dialog starts from: the member's, or the group card's. */
  current: ReviewOutcome;
  /** The suggestions Save changes, one `PUT` each. */
  members: SuggestionDto[];
  /** A group edit; `truncated` when the card does not list every member. */
  group: { display: string; truncated: boolean } | null;
}

/** Groups larger than this show a progress bar while saving. */
export const EDIT_PROGRESS_THRESHOLD = 20;

/** Edits the label and flags of one suggestion or of every listed member of a group. */
@Component({
  selector: 'app-edit-suggestion-dialog',
  imports: [
    LabelTreePicker,
    MatButtonModule,
    MatDialogModule,
    MatFormFieldModule,
    MatInputModule,
    MatProgressBarModule,
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

  readonly form = new FormGroup({
    topicLabel: new FormControl(this.data.current.topicLabel, {
      nonNullable: true,
      validators: [labelPathValidator],
    }),
    needsAction: new FormControl(this.data.current.needsAction, { nonNullable: true }),
    toBeDeleted: new FormControl(this.data.current.toBeDeleted && !this.allProtected(), {
      nonNullable: true,
    }),
  });

  readonly flagLabels = toSignal(
    inject(SettingsService)
      .getSettings()
      .pipe(
        map((s) => ({ action: s.actionLabelName, delete: s.deleteLabelName })),
        catchError(() => of(DEFAULT_FLAG_LABELS)),
      ),
    { initialValue: DEFAULT_FLAG_LABELS },
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

  readonly protectedCount = this.data.members.filter((m) => m.protected).length;
  readonly total = this.data.members.length;
  readonly saving = signal(false);
  readonly done = signal(0);
  readonly showProgress = this.total > EDIT_PROGRESS_THRESHOLD;
  readonly error = signal<string | null>(null);

  constructor() {
    if (this.allProtected()) this.form.controls.toBeDeleted.disable();
    this.loadLabels(this.labelsApi.labels());
  }

  allProtected(): boolean {
    return this.data.members.length > 0 && this.data.members.every((m) => m.protected);
  }

  pathError(): string | null {
    const errors = this.form.controls.topicLabel.errors;
    return (errors?.['labelPath'] ?? errors?.['server'] ?? null) as string | null;
  }

  pickLabel(path: string): void {
    this.form.controls.topicLabel.setValue(path);
    this.form.controls.topicLabel.markAsTouched();
  }

  refreshLabels(): void {
    this.loadLabels(this.labelsApi.refresh());
  }

  /** `PUT` per member in order; stops at the first error and stays open with it. */
  save(): void {
    if (this.saving()) return;
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    const outcome = this.form.getRawValue();
    this.saving.set(true);
    this.error.set(null);
    from(this.data.members)
      .pipe(
        concatMap((m) => this.review.edit(m.id, editRequest(m, outcome))),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe({
        next: () => this.done.update((d) => d + 1),
        complete: () => this.ref.close(true),
        error: (err: unknown) => this.failed(err),
      });
  }

  cancel(): void {
    this.ref.close(this.done() > 0);
  }

  private failed(err: unknown): void {
    this.saving.set(false);
    const fields = err instanceof HttpErrorResponse ? fieldErrors(err) : {};
    const labelError = fields['topicLabel']?.[0];
    if (labelError) {
      this.form.controls.topicLabel.setErrors({ server: labelError });
      this.form.controls.topicLabel.markAsTouched();
    }
    const saved = this.done() ? ` ${this.done()} of ${this.total} were saved.` : '';
    const message = err instanceof HttpErrorResponse ? errorMessage(err) : 'Saving failed.';
    this.error.set(labelError && !saved ? null : `${message}${saved}`);
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
