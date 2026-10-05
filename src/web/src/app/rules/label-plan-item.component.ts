import { DecimalPipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  input,
  output,
  signal,
} from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormControl, ReactiveFormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatChipsModule } from '@angular/material/chips';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { RouterLink } from '@angular/router';
import { LabelTreePicker } from '../review/label-tree-picker.component';
import { LabelDto } from '../review/labels.models';
import { labelPathValidator } from '../review/labels.service';
import {
  createName,
  isTaxonomyItem,
  LabelPlanItemDto,
  proposalText,
  UpdatePlanItemRequest,
} from './label-plan.models';

/**
 * One plan item: accept / reject, the editable nest or new-label name or merge target, filters,
 * rationale and outcome; a taxonomy item also shows its description and senders.
 */
@Component({
  selector: 'app-label-plan-item',
  imports: [
    DecimalPipe,
    LabelTreePicker,
    MatButtonModule,
    MatButtonToggleModule,
    MatChipsModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    ReactiveFormsModule,
    RouterLink,
  ],
  template: `
    @let i = item();
    <div class="flex flex-col gap-2 py-3" data-testid="plan-item" [attr.data-kind]="i.kind">
      <div class="flex flex-wrap items-center gap-x-3 gap-y-2">
        <span class="font-medium break-all" [class.line-through]="i.kind === 'empty'">{{
          i.labelName
        }}</span>
        <span class="text-sm break-all" data-testid="plan-item-proposal">{{ proposal(i) }}</span>
        <span class="muted text-sm">{{ i.messageCount | number }} messages</span>
        @if (i.affectedFilterIds.length; as n) {
          <a
            class="text-sm"
            [routerLink]="[]"
            [queryParams]="{ tab: 'filters' }"
            queryParamsHandling="merge"
            data-testid="plan-item-filters"
            >{{ n }} {{ n === 1 ? 'filter' : 'filters' }} affected</a
          >
        }
        <span class="ml-auto"></span>
        @if (i.status === 'applied' || i.status === 'failed') {
          <mat-chip-set [attr.aria-label]="'Outcome of ' + i.labelName">
            <mat-chip [class.failed]="i.status === 'failed'" data-testid="plan-item-status">{{
              i.status
            }}</mat-chip>
          </mat-chip-set>
        } @else {
          <mat-button-toggle-group
            hideSingleSelectionIndicator
            [value]="decision()"
            [disabled]="!editable()"
            (change)="decide($event.value)"
            [attr.aria-label]="'Decision on ' + i.labelName"
          >
            <mat-button-toggle value="accepted" data-testid="plan-item-accept"
              >Accept</mat-button-toggle
            >
            <mat-button-toggle value="rejected" data-testid="plan-item-reject"
              >Reject</mat-button-toggle
            >
          </mat-button-toggle-group>
        }
      </div>
      @if (i.error) {
        <p class="error m-0 flex items-center gap-1 text-sm" data-testid="plan-item-error">
          <mat-icon aria-hidden="true">error</mat-icon>{{ i.error }}
        </p>
      }
      @if (taxonomy() && i.kind === 'near_duplicate') {
        <p class="warn m-0 flex items-center gap-1 text-sm" data-testid="plan-item-near-duplicate">
          <mat-icon aria-hidden="true">warning</mat-icon>Nearly duplicates
          "{{ i.targetLabelName ?? i.targetLabelId }}": its senders go there instead of a new label.
        </p>
      }
      @if (i.description) {
        <p class="m-0 text-sm" data-testid="plan-item-description">{{ i.description }}</p>
      }
      <p class="muted m-0 text-sm" data-testid="plan-item-rationale">{{ i.rationale }}</p>
      @if (i.senderKeys; as senders) {
        <div>
          <button
            mat-button
            type="button"
            [attr.aria-expanded]="showSenders()"
            [attr.aria-controls]="'senders-' + i.id"
            (click)="showSenders.set(!showSenders())"
            data-testid="plan-item-senders-toggle"
          >
            <mat-icon aria-hidden="true">{{ showSenders() ? 'expand_less' : 'expand_more' }}</mat-icon
            >{{ senders.length | number }} {{ senders.length === 1 ? 'sender' : 'senders' }}
          </button>
          @if (showSenders()) {
            <ul
              class="m-0 pl-8 text-sm break-all"
              [id]="'senders-' + i.id"
              data-testid="plan-item-senders"
            >
              @for (sender of senders; track sender) {
                <li>{{ sender }}</li>
              }
            </ul>
          }
        </div>
      }

      @if (nameEditable() && editable()) {
        <form class="flex flex-wrap items-start gap-2" (submit)="saveName($event)">
          <mat-form-field class="min-w-64 flex-1" subscriptSizing="dynamic">
            <mat-label>{{ i.kind === 'create' ? 'Label name' : 'New name' }}</mat-label>
            <input matInput [formControl]="name" data-testid="plan-item-name" />
            @if (name.errors?.['labelPath']; as error) {
              <mat-error data-testid="plan-item-name-error">{{ error }}</mat-error>
            }
          </mat-form-field>
          <button
            mat-button
            type="submit"
            class="mt-2"
            [disabled]="!nameChanged() || name.invalid"
            data-testid="plan-item-save-name"
          >
            Save name
          </button>
        </form>
      }
      @if (i.kind === 'near_duplicate' && editable()) {
        <div>
          <button
            mat-button
            type="button"
            [attr.aria-expanded]="picking()"
            (click)="picking.set(!picking())"
            data-testid="plan-item-change-target"
          >
            Change target
          </button>
          @if (picking()) {
            <app-label-tree-picker
              [labels]="labels()"
              [selected]="i.targetLabelName ?? ''"
              (pick)="pickTarget($event)"
            />
          }
        </div>
      }
    </div>
  `,
  styles: `
    .muted {
      color: var(--mat-sys-on-surface-variant);
    }
    .warn {
      color: var(--mat-sys-tertiary);
    }
    .error,
    mat-chip.failed {
      color: var(--mat-sys-error);
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LabelPlanItem {
  readonly item = input.required<LabelPlanItemDto>();
  /** The user's labels, for the merge target picker. */
  readonly labels = input<readonly LabelDto[]>([]);
  /** False unless the plan is a draft and no edit of this item is in flight. */
  readonly editable = input(false);
  readonly update = output<UpdatePlanItemRequest>();

  readonly proposal = proposalText;
  readonly name = new FormControl('', { nonNullable: true, validators: labelPathValidator });
  private readonly nameValue = toSignal(this.name.valueChanges, { initialValue: '' });
  /** Changes only when the stored name does, not when a plan update replaces the item object. */
  private readonly proposedName = computed(() => {
    const item = this.item();
    return item.kind === 'create' ? createName(item) : (item.proposedName ?? '');
  });
  readonly taxonomy = computed(() => isTaxonomyItem(this.item()));
  /** A nest item's new name, or the name of a label a `create` item makes. */
  readonly nameEditable = computed(() => {
    const item = this.item();
    return item.kind === 'nest' || (item.kind === 'create' && !item.labelId);
  });
  readonly showSenders = signal(false);
  readonly nameChanged = computed(() => this.nameValue().trim() !== this.proposedName());
  readonly picking = signal(false);
  readonly decision = computed(() => {
    const status = this.item().status;
    return status === 'accepted' || status === 'rejected' ? status : null;
  });

  constructor() {
    // A saved or reloaded name replaces the input; other plan updates keep an unsaved edit.
    effect(() => this.name.setValue(this.proposedName()));
  }

  decide(status: 'accepted' | 'rejected'): void {
    this.update.emit({ status });
  }

  saveName(event: Event): void {
    event.preventDefault();
    if (this.name.invalid || !this.nameChanged()) return;
    this.update.emit({ proposedName: this.name.value.trim() });
  }

  /** The picker gives a path; the API wants the label's id. */
  pickTarget(path: string): void {
    const label = this.labels().find((l) => l.type === 'user' && l.name === path);
    this.picking.set(false);
    if (!label || label.id === this.item().targetLabelId) return;
    this.update.emit({ targetLabelId: label.id });
  }
}
