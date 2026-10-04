import { HttpErrorResponse } from '@angular/common/http';
import { DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import {
  AbstractControl,
  FormControl,
  FormGroup,
  ReactiveFormsModule,
  ValidationErrors,
} from '@angular/forms';
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
import { catchError, debounceTime, EMPTY, map, Observable, of, startWith, switchMap } from 'rxjs';
import { errorMessage } from '../core/error.interceptor';
import { fieldErrors } from '../review/edit-suggestion-dialog.component';
import { LabelTreePicker } from '../review/label-tree-picker.component';
import { LabelDto } from '../review/labels.models';
import { labelPathError, LabelsService } from '../review/labels.service';
import { SettingsService } from '../settings/settings.service';
import { FilterDto, FilterEdit, FilterPreviewDto, FilterRequest } from './rules.models';
import { filterEdit, filterRequest, filterRequestProblem, RulesService } from './rules.service';

export interface FilterPreviewDialogData {
  /** The proposal's suggested filter; null opens the dialog with just `from` filled. */
  request: FilterRequest | null;
  from: string;
}

/** Live preview requests wait this long after the last change. */
export const PREVIEW_DEBOUNCE_MS = 400;

/** The label path is optional (skip inbox alone is a filter); when given it must be valid. */
function optionalLabelPath(control: AbstractControl): ValidationErrors | null {
  const value = String(control.value ?? '');
  const error = value.trim() ? labelPathError(value) : null;
  return error ? { labelPath: error } : null;
}

/** Edits a filter's criteria and actions with a live dry-run preview, then creates it in Gmail. */
@Component({
  selector: 'app-filter-preview-dialog',
  imports: [
    DecimalPipe,
    LabelTreePicker,
    MatButtonModule,
    MatCheckboxModule,
    MatDialogModule,
    MatFormFieldModule,
    MatInputModule,
    MatProgressBarModule,
    MatSelectModule,
    MatSlideToggleModule,
    ReactiveFormsModule,
  ],
  templateUrl: './filter-preview-dialog.component.html',
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
export class FilterPreviewDialog {
  readonly data = inject<FilterPreviewDialogData>(MAT_DIALOG_DATA);
  private readonly ref = inject<MatDialogRef<FilterPreviewDialog, FilterDto>>(MatDialogRef);
  private readonly rules = inject(RulesService);
  private readonly labelsApi = inject(LabelsService);
  private readonly destroyRef = inject(DestroyRef);

  private readonly initial = filterEdit(this.data.request, this.data.from, []);
  readonly form = new FormGroup({
    from: new FormControl(this.initial.from, { nonNullable: true }),
    to: new FormControl(this.initial.to, { nonNullable: true }),
    subject: new FormControl(this.initial.subject, { nonNullable: true }),
    hasAttachment: new FormControl(this.initial.hasAttachment, { nonNullable: true }),
    query: new FormControl(this.initial.query, { nonNullable: true }),
    negatedQuery: new FormControl(this.initial.negatedQuery, { nonNullable: true }),
    labelPath: new FormControl(this.initial.labelPath, {
      nonNullable: true,
      validators: [optionalLabelPath],
    }),
    archiveLabel: new FormControl(this.initial.archiveLabel, { nonNullable: true }),
    skipInbox: new FormControl(this.initial.skipInbox, { nonNullable: true }),
    markRead: new FormControl(this.initial.markRead, { nonNullable: true }),
  });

  /** The labels of the auto-archive rules (`settings.appsScript.rules`); empty when none. */
  readonly archiveLabels = signal<string[]>([]);
  readonly labels = signal<LabelDto[] | null>(null);
  readonly labelsFailed = signal(false);
  readonly path = toSignal(this.form.controls.labelPath.valueChanges, {
    initialValue: this.form.controls.labelPath.value,
  });

  readonly previewing = signal(false);
  readonly preview = signal<FilterPreviewDto | null>(null);
  /** Why there is no preview: an incomplete form or the server's validation errors. */
  readonly problems = signal<string[]>([]);
  readonly saving = signal(false);
  readonly error = signal<string | null>(null);

  constructor() {
    inject(SettingsService)
      .getSettings()
      .pipe(
        map((s) => [...new Set((s.appsScript?.rules ?? []).map((r) => r.label))]),
        catchError(() => of([])),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((labels) => {
        this.archiveLabels.set(labels);
        const archive = filterEdit(this.data.request, this.data.from, labels).archiveLabel;
        if (archive && !this.form.controls.archiveLabel.value) {
          this.form.controls.archiveLabel.setValue(archive);
        }
      });

    this.form.valueChanges
      .pipe(
        debounceTime(PREVIEW_DEBOUNCE_MS),
        startWith(null),
        switchMap(() => this.previewRequest()),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((preview) => {
        this.previewing.set(false);
        this.preview.set(preview);
      });

    this.loadLabels(this.labelsApi.labels());
  }

  pathError(): string | null {
    return (this.form.controls.labelPath.errors?.['labelPath'] as string | undefined) ?? null;
  }

  pickLabel(path: string): void {
    this.form.controls.labelPath.setValue(path);
    this.form.controls.labelPath.markAsTouched();
  }

  refreshLabels(): void {
    this.loadLabels(this.labelsApi.refresh());
  }

  /** Creates the filter; the dialog closes with it. Gmail's refusal (409 / 503) shows in a snackbar. */
  create(): void {
    if (this.saving()) return;
    const request = this.request();
    if (!request) {
      this.form.markAllAsTouched();
      return;
    }
    this.saving.set(true);
    this.ref.disableClose = true;
    this.error.set(null);
    this.rules
      .create(request)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (filter) => this.ref.close(filter),
        error: (err: unknown) => {
          this.saving.set(false);
          this.ref.disableClose = false;
          this.error.set(err instanceof HttpErrorResponse ? errorMessage(err) : 'Creating failed.');
        },
      });
  }

  cancel(): void {
    if (!this.saving()) this.ref.close();
  }

  /** The request when the form can be sent; otherwise the problems say why not. */
  private request(): FilterRequest | null {
    if (this.form.invalid) {
      this.problems.set([this.pathError() ?? 'Check the highlighted fields.']);
      return null;
    }
    const request = filterRequest(this.form.getRawValue() satisfies FilterEdit);
    const problem = filterRequestProblem(request);
    this.problems.set(problem ? [problem] : []);
    return problem ? null : request;
  }

  private previewRequest(): Observable<FilterPreviewDto | null> {
    const request = this.request();
    if (!request) return of(null);
    this.previewing.set(true);
    return this.rules.preview(request).pipe(
      catchError((err: unknown) => {
        const errors = err instanceof HttpErrorResponse ? fieldErrors(err) : {};
        const lines = Object.values(errors).flat();
        this.problems.set(lines.length ? lines : ['The preview could not be loaded.']);
        this.previewing.set(false);
        this.preview.set(null);
        return EMPTY;
      }),
    );
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

/** Opens the preview dialog; emits the created filter, or `undefined` when cancelled. */
export function openFilterPreview(
  dialog: MatDialog,
  data: FilterPreviewDialogData,
): Observable<FilterDto | undefined> {
  return dialog
    .open<FilterPreviewDialog, FilterPreviewDialogData, FilterDto>(FilterPreviewDialog, {
      data,
      width: '40rem',
      maxWidth: '95vw',
      autoFocus: 'first-tabbable',
    })
    .afterClosed();
}
