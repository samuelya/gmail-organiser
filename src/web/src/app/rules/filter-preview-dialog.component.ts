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
import {
  catchError,
  debounceTime,
  distinctUntilChanged,
  EMPTY,
  map,
  Observable,
  of,
  startWith,
  switchMap,
} from 'rxjs';
import { errorMessage } from '../core/error.interceptor';
import { fieldErrors } from '../review/edit-suggestion-dialog.component';
import { ExtraLabelsField } from '../review/extra-labels-field.component';
import { LabelTreePicker } from '../review/label-tree-picker.component';
import { LabelDto } from '../review/labels.models';
import { labelPathError, LabelsService } from '../review/labels.service';
import { MAX_EXTRA_LABELS } from '../review/review.models';
import { SettingsService } from '../settings/settings.service';
import { FilterDto, FilterEdit, FilterPreviewDto, FilterRequest } from './rules.models';
import { filterEdit, filterRequest, filterRequestProblem, RulesService } from './rules.service';

export interface FilterPreviewDialogData {
  /** The proposal's suggested filter; null opens the dialog with just `from` filled. */
  request: FilterRequest | null;
  from: string;
  /** The sender's approved topic label, shown as a hint when there is no `request`. */
  labelHint?: string | null;
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
    ExtraLabelsField,
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
  /** The proposal's additional labels left out of the field because there were more than the field takes. */
  readonly maxExtras = MAX_EXTRA_LABELS;
  readonly droppedExtras = signal(Math.max(0, this.initial.extraLabels.length - MAX_EXTRA_LABELS));
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
    extraLabels: new FormControl(
      {
        value: this.initial.extraLabels.slice(0, MAX_EXTRA_LABELS),
        disabled: !this.initial.labelPath.trim(),
      },
      { nonNullable: true },
    ),
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
  readonly archive = toSignal(this.form.controls.archiveLabel.valueChanges, {
    initialValue: this.form.controls.archiveLabel.value,
  });

  readonly previewing = signal(false);
  readonly preview = signal<FilterPreviewDto | null>(null);
  /** Why there is no preview: an incomplete form or the server's validation errors. */
  readonly problems = signal<string[]>([]);
  readonly saving = signal(false);
  readonly error = signal<string | null>(null);

  constructor() {
    // A chip equal to the archive label would clash with it; the request adds that label anyway.
    this.form.controls.archiveLabel.valueChanges
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((archive) => {
        const key = archive.trim().toLowerCase();
        const extras = this.form.controls.extraLabels;
        if (key && extras.value.some((n) => n.toLowerCase() === key)) {
          extras.setValue(extras.value.filter((n) => n.toLowerCase() !== key));
        }
      });

    inject(SettingsService)
      .getSettings()
      .pipe(
        map((s) => [...new Set((s.appsScript?.rules ?? []).map((r) => r.label))]),
        catchError(() => of([])),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((labels) => {
        this.archiveLabels.set(labels);
        const edit = filterEdit(this.data.request, this.data.from, labels);
        if (edit.archiveLabel && !this.form.controls.archiveLabel.value) {
          // The proposal's archive label was an additional label until the rules were known; untouched
          // extras get the slot it frees.
          const extras = this.form.controls.extraLabels;
          if (extras.pristine) {
            extras.setValue(edit.extraLabels.slice(0, MAX_EXTRA_LABELS));
            this.droppedExtras.set(Math.max(0, edit.extraLabels.length - MAX_EXTRA_LABELS));
          }
          this.form.controls.archiveLabel.setValue(edit.archiveLabel);
        }
      });

    // An additional label needs a main label.
    this.form.controls.labelPath.valueChanges
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((path) => {
        const extras = this.form.controls.extraLabels;
        if (path.trim()) extras.enable({ emitEvent: false });
        else extras.disable({ emitEvent: false });
      });

    // Text typed into the extra labels but not added yet re-validates the form without changing
    // the request; the same request is not previewed twice.
    this.form.valueChanges
      .pipe(
        debounceTime(PREVIEW_DEBOUNCE_MS),
        startWith(null),
        map(() => this.request(true)),
        distinctUntilChanged((a, b) => !!a && !!b && sameRequest(a, b)),
        switchMap((request) => this.previewRequest(request)),
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

  /**
   * The request when the form can be sent; otherwise the problems say why not. A preview uses the
   * added chips while extra-label text is still being typed; only Create waits for it.
   */
  private request(preview = false): FilterRequest | null {
    if (this.form.invalid && !(preview && this.onlyTyping())) {
      this.problems.set([this.pathError() ?? 'Check the highlighted fields.']);
      return null;
    }
    const request = filterRequest(this.form.getRawValue() satisfies FilterEdit);
    const problem = filterRequestProblem(request);
    this.problems.set(problem ? [problem] : []);
    return problem ? null : request;
  }

  /** The only error is extra-label text not added yet. */
  private onlyTyping(): boolean {
    const { extraLabels, ...rest } = this.form.controls;
    const errors = extraLabels.errors ?? {};
    return (
      Object.values(rest).every((c) => c.valid || c.disabled) &&
      Object.keys(errors).every((k) => k === 'extraLabels' || k === 'extraLabelsPending') &&
      errors['extraLabelsPending'] === true
    );
  }

  private previewRequest(request: FilterRequest | null): Observable<FilterPreviewDto | null> {
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

function sameRequest(a: FilterRequest, b: FilterRequest): boolean {
  return JSON.stringify(a) === JSON.stringify(b);
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
