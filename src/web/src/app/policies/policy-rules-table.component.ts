import { DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { AbstractControl, ReactiveFormsModule } from '@angular/forms';
import { MatAutocompleteModule } from '@angular/material/autocomplete';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatTooltipModule } from '@angular/material/tooltip';
import { switchMap } from 'rxjs';
import {
  controlError,
  label,
  MAIL_TYPES,
  matchingOptions,
  matchValue,
  matchSummary,
  MESSAGE_CATEGORIES,
  moveRule,
  POLICY_ACTIONS,
  PolicyForm,
  PolicyRuleDto,
  RuleForm,
  ruleForm,
} from './policies.models';

/** A per-rule approve or reject. */
export interface RuleDecision {
  ruleId: string;
  decision: 'approve' | 'reject';
}

/**
 * The policy's sub-rules in order, first match wins: their preview from the API (status, match count,
 * sample subjects) and an edit panel per rule. Changes stay in the form until the page saves.
 */
@Component({
  selector: 'app-policy-rules-table',
  imports: [
    DecimalPipe,
    MatAutocompleteModule,
    MatButtonModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatSelectModule,
    MatTooltipModule,
    ReactiveFormsModule,
  ],
  templateUrl: './policy-rules-table.component.html',
  styles: `
    .muted {
      color: var(--mat-sys-on-surface-variant);
    }
    .error {
      color: var(--mat-sys-error);
    }
    th {
      text-align: start;
      font: var(--mat-sys-title-small);
    }
    td,
    th {
      padding: 0.25rem 0.5rem;
      border-bottom: 1px solid var(--mat-sys-outline-variant);
      vertical-align: top;
    }
    .status {
      padding: 0 0.5rem;
      border-radius: 9999px;
      font: var(--mat-sys-label-medium);
      line-height: 1.5rem;
      white-space: nowrap;
      background: var(--mat-sys-surface-container-highest);
    }
    .status-approved {
      background: var(--mat-sys-primary-container);
      color: var(--mat-sys-on-primary-container);
    }
    .status-rejected {
      background: var(--mat-sys-error-container);
      color: var(--mat-sys-on-error-container);
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PolicyRulesTable {
  readonly form = input.required<PolicyForm>();
  /** The saved rules with their preview, by id. */
  readonly saved = input<readonly PolicyRuleDto[]>([]);
  readonly labels = input<readonly string[]>([]);
  readonly documentTypes = input<readonly string[]>([]);
  /** Per-rule decisions wait for this to be false (unsaved edits or a request in flight). */
  readonly decisionsDisabled = input(false);
  /** A rejected policy: no edits and no decisions. */
  readonly readonly = input(false);
  readonly decide = output<RuleDecision>();

  readonly changes = toSignal(toObservable(this.form).pipe(switchMap((f) => f.events)));
  readonly savedById = computed(() => new Map(this.saved().map((r) => [r.id, r])));
  /** Rules whose edit panel is open. */
  readonly expanded = signal<ReadonlySet<RuleForm>>(new Set());
  readonly mailTypes = MAIL_TYPES;
  readonly actions = POLICY_ACTIONS;
  readonly categories = MESSAGE_CATEGORIES;
  readonly label = label;
  /** The control's error message; read on every form event, so server errors show. */
  error(control: AbstractControl): string | null {
    this.changes();
    return controlError(control);
  }
  readonly options = matchingOptions;

  savedRule(rule: RuleForm): PolicyRuleDto | undefined {
    const id = rule.controls.id.value;
    return id ? this.savedById().get(id) : undefined;
  }

  /** The rule's match as it stands in the form. */
  summary(rule: RuleForm): string {
    this.changes();
    return matchSummary(matchValue(rule.controls.match.getRawValue()));
  }

  /** Open when toggled, and while it holds an error the user should see. */
  isOpen(rule: RuleForm): boolean {
    this.changes();
    return this.expanded().has(rule) || (rule.invalid && rule.touched);
  }

  toggle(rule: RuleForm): void {
    this.expanded.update((open) => {
      const next = new Set(open);
      if (!next.delete(rule)) next.add(rule);
      return next;
    });
  }

  move(index: number, delta: -1 | 1): void {
    moveRule(this.form().controls.rules, index, delta);
  }

  add(): void {
    const rule = ruleForm();
    this.form().controls.rules.push(rule);
    this.form().controls.rules.markAsDirty();
    this.expanded.update((open) => new Set([...open, rule]));
  }

  remove(index: number): void {
    const rules = this.form().controls.rules;
    const rule = rules.at(index);
    rules.removeAt(index);
    rules.markAsDirty();
    this.expanded.update((open) => new Set([...open].filter((r) => r !== rule)));
  }
}
