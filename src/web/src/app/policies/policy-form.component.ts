import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { AbstractControl, ReactiveFormsModule } from '@angular/forms';
import { MatAutocompleteModule } from '@angular/material/autocomplete';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { switchMap } from 'rxjs';
import {
  controlError,
  label,
  MAIL_TYPES,
  matchingOptions,
  POLICY_ACTIONS,
  PolicyForm,
} from './policies.models';

/** The policy's own fields: its default outcome and whether it is mixed. Rules are the rules table's. */
@Component({
  selector: 'app-policy-form',
  imports: [
    MatAutocompleteModule,
    MatFormFieldModule,
    MatInputModule,
    MatSelectModule,
    MatSlideToggleModule,
    ReactiveFormsModule,
  ],
  template: `
    @let f = form();
    <div class="grid grid-cols-1 gap-x-4 gap-y-2 md:grid-cols-2" [formGroup]="f">
      <mat-form-field>
        <mat-label>Label</mat-label>
        <input
          matInput
          formControlName="topicLabel"
          [matAutocomplete]="topics"
          data-testid="policy-label"
        />
        <mat-autocomplete #topics="matAutocomplete">
          @for (l of options(labels(), f.controls.topicLabel.value); track l) {
            <mat-option [value]="l">{{ l }}</mat-option>
          }
        </mat-autocomplete>
        @if (error(f.controls.topicLabel); as e) {
          <mat-error data-testid="policy-label-error">{{ e }}</mat-error>
        } @else {
          <mat-hint>An existing label or a new path; '/' nests it.</mat-hint>
        }
      </mat-form-field>

      @if (documentTypeParent(); as parent) {
        <mat-form-field>
          <mat-label>Document type</mat-label>
          <input
            matInput
            formControlName="documentTypeLabel"
            [matAutocomplete]="types"
            placeholder="None"
            data-testid="policy-document-type"
          />
          <mat-autocomplete #types="matAutocomplete">
            <mat-option value="">None</mat-option>
            @for (t of options(documentTypes(), f.controls.documentTypeLabel.value); track t) {
              <mat-option [value]="t">{{ t }}</mat-option>
            }
          </mat-autocomplete>
          @if (error(f.controls.documentTypeLabel); as e) {
            <mat-error data-testid="policy-document-type-error">{{ e }}</mat-error>
          } @else {
            <mat-hint>A label under {{ parent }}, or blank for none.</mat-hint>
          }
        </mat-form-field>
      }

      <mat-form-field>
        <mat-label>Mail type</mat-label>
        <mat-select formControlName="mailType" data-testid="policy-mail-type">
          <mat-option value="">None</mat-option>
          @for (t of mailTypes; track t) {
            <mat-option [value]="t">{{ label(t) }}</mat-option>
          }
        </mat-select>
        @if (error(f.controls.mailType); as e) {
          <mat-error>{{ e }}</mat-error>
        }
      </mat-form-field>

      <mat-form-field>
        <mat-label>Retention days</mat-label>
        <input
          matInput
          type="number"
          min="1"
          formControlName="retentionDays"
          data-testid="policy-retention"
        />
        @if (error(f.controls.retentionDays); as e) {
          <mat-error data-testid="policy-retention-error">{{ e }}</mat-error>
        } @else {
          <mat-hint>Blank: the mail type's default.</mat-hint>
        }
      </mat-form-field>

      <mat-form-field>
        <mat-label>Action</mat-label>
        <mat-select formControlName="action" data-testid="policy-action">
          @for (a of actions; track a) {
            <mat-option [value]="a">{{ a }}</mat-option>
          }
        </mat-select>
        @if (error(f.controls.action); as e) {
          <mat-error data-testid="policy-action-error">{{ e }}</mat-error>
        }
      </mat-form-field>

      <div class="flex flex-col justify-center">
        <mat-slide-toggle formControlName="isMixed" data-testid="policy-mixed"
          >Mixed sender</mat-slide-toggle
        >
        <span class="muted text-sm">
          Rules decide its mail; unmatched mail goes to analysis and review.
        </span>
        @if (error(f.controls.isMixed); as e) {
          <span class="error text-sm" role="alert">{{ e }}</span>
        }
      </div>
    </div>
  `,
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
export class PolicyFormComponent {
  readonly form = input.required<PolicyForm>();
  /** Gmail label names for the autocomplete. */
  readonly labels = input<readonly string[]>([]);
  /** Document-type labels (full paths) under `documentTypeParent`. */
  readonly documentTypes = input<readonly string[]>([]);
  /** From settings; null hides the document-type field. */
  readonly documentTypeParent = input<string | null>(null);

  /** Every value, status or touched change: errors set from the server show without a new input. */
  readonly changes = toSignal(toObservable(this.form).pipe(switchMap((f) => f.events)));
  readonly mailTypes = MAIL_TYPES;
  readonly actions = POLICY_ACTIONS;
  readonly label = label;
  /** The control's error message; read on every form event, so server errors show. */
  error(control: AbstractControl): string | null {
    this.changes();
    return controlError(control);
  }
  readonly options = matchingOptions;
}
