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
  template: `
    @let rules = form().controls.rules;
    <div class="flex flex-col gap-2">
      <div class="flex items-center gap-2">
        <h3 class="m-0 flex-1 text-base font-medium" id="rules-heading">Rules</h3>
        <button
          mat-stroked-button
          type="button"
          [disabled]="readonly()"
          (click)="add()"
          data-testid="rule-add"
        >
          <mat-icon aria-hidden="true">add</mat-icon>
          Add rule
        </button>
      </div>
      <p class="muted m-0 text-sm">
        Checked in order: the first rule that matches decides the message.
      </p>
      @if (error(rules); as e) {
        <p class="error m-0 text-sm" role="alert">{{ e }}</p>
      }

      @if (rules.length === 0) {
        <p class="muted m-0" data-testid="rules-empty">
          No rules: the policy's own outcome decides every message.
        </p>
      } @else {
        <div class="overflow-x-auto">
          <table class="w-full border-collapse" aria-labelledby="rules-heading">
            <thead>
              <tr>
                <th scope="col">Order</th>
                <th scope="col">Name</th>
                <th scope="col">Match</th>
                <th scope="col">Outcome</th>
                <th scope="col">Status</th>
                <th scope="col">Matches</th>
                <th scope="col"><span class="sr-only">Actions</span></th>
              </tr>
            </thead>
            <tbody>
              @for (
                rule of rules.controls;
                track rule;
                let i = $index, first = $first, last = $last
              ) {
                @let saved = savedRule(rule);
                @let v = rule.getRawValue();
                <tr data-testid="rule-row">
                  <td class="whitespace-nowrap">
                    <button
                      mat-icon-button
                      type="button"
                      [disabled]="first || readonly()"
                      (click)="move(i, -1)"
                      [attr.aria-label]="'Move ' + (v.name || 'rule') + ' up'"
                      data-testid="rule-up"
                    >
                      <mat-icon aria-hidden="true">arrow_upward</mat-icon>
                    </button>
                    <button
                      mat-icon-button
                      type="button"
                      [disabled]="last || readonly()"
                      (click)="move(i, 1)"
                      [attr.aria-label]="'Move ' + (v.name || 'rule') + ' down'"
                      data-testid="rule-down"
                    >
                      <mat-icon aria-hidden="true">arrow_downward</mat-icon>
                    </button>
                  </td>
                  <td data-testid="rule-name">
                    {{ v.name || '(unnamed)' }}
                    @if (saved?.reason; as reason) {
                      <div class="muted text-sm">{{ reason }}</div>
                    }
                  </td>
                  <td data-testid="rule-match">{{ summary(rule) }}</td>
                  <td>
                    {{ v.topicLabel }} · {{ v.action }}
                    @if (v.mailType) {
                      · {{ label(v.mailType) }}
                    }
                  </td>
                  <td>
                    @if (saved) {
                      <span
                        class="status"
                        [class]="'status-' + saved.status"
                        data-testid="rule-status"
                        >{{ saved.status }}</span
                      >
                    } @else {
                      <span class="status" data-testid="rule-status">new</span>
                    }
                  </td>
                  <td class="whitespace-nowrap" data-testid="rule-count">
                    {{ saved ? (saved.matchCount | number) : '–' }}
                  </td>
                  <td class="whitespace-nowrap">
                    <button
                      mat-icon-button
                      type="button"
                      (click)="toggle(rule)"
                      [attr.aria-expanded]="isOpen(rule)"
                      [attr.aria-label]="(isOpen(rule) ? 'Close ' : 'Open ') + (v.name || 'rule')"
                      data-testid="rule-toggle"
                    >
                      <mat-icon aria-hidden="true">{{
                        isOpen(rule) ? 'expand_less' : 'edit'
                      }}</mat-icon>
                    </button>
                    @if (saved && saved.status !== 'approved' && !readonly()) {
                      <button
                        mat-icon-button
                        type="button"
                        [disabled]="decisionsDisabled()"
                        (click)="decide.emit({ ruleId: saved.id, decision: 'approve' })"
                        [matTooltip]="
                          decisionsDisabled() ? 'Save or discard changes first' : 'Approve rule'
                        "
                        [attr.aria-label]="'Approve ' + saved.name"
                        data-testid="rule-approve"
                      >
                        <mat-icon aria-hidden="true">check</mat-icon>
                      </button>
                    }
                    @if (saved && saved.status !== 'rejected' && !readonly()) {
                      <button
                        mat-icon-button
                        type="button"
                        [disabled]="decisionsDisabled()"
                        (click)="decide.emit({ ruleId: saved.id, decision: 'reject' })"
                        [matTooltip]="
                          decisionsDisabled() ? 'Save or discard changes first' : 'Reject rule'
                        "
                        [attr.aria-label]="'Reject ' + saved.name"
                        data-testid="rule-reject"
                      >
                        <mat-icon aria-hidden="true">block</mat-icon>
                      </button>
                    }
                    <button
                      mat-icon-button
                      type="button"
                      [disabled]="readonly()"
                      (click)="remove(i)"
                      [attr.aria-label]="'Remove ' + (v.name || 'rule')"
                      data-testid="rule-remove"
                    >
                      <mat-icon aria-hidden="true">delete_outline</mat-icon>
                    </button>
                  </td>
                </tr>
                @if (isOpen(rule)) {
                  <tr data-testid="rule-panel">
                    <td colspan="7">
                      @if (saved?.sampleSubjects?.length) {
                        <div class="mb-2">
                          <span class="text-sm font-medium">Sample subjects</span>
                          <ul class="m-0 pl-5 text-sm" data-testid="rule-samples">
                            @for (s of saved!.sampleSubjects; track $index) {
                              <li>{{ s }}</li>
                            }
                          </ul>
                        </div>
                      }
                      <div class="grid grid-cols-1 gap-x-4 md:grid-cols-3" [formGroup]="rule">
                        <mat-form-field>
                          <mat-label>Name</mat-label>
                          <input matInput formControlName="name" data-testid="rule-name-input" />
                          @if (error(rule.controls.name); as e) {
                            <mat-error>{{ e }}</mat-error>
                          }
                        </mat-form-field>
                        <mat-form-field>
                          <mat-label>Label</mat-label>
                          <input
                            matInput
                            formControlName="topicLabel"
                            [matAutocomplete]="ruleTopics"
                            data-testid="rule-label-input"
                          />
                          <mat-autocomplete #ruleTopics="matAutocomplete">
                            @for (l of options(labels(), v.topicLabel); track l) {
                              <mat-option [value]="l">{{ l }}</mat-option>
                            }
                          </mat-autocomplete>
                          @if (error(rule.controls.topicLabel); as e) {
                            <mat-error data-testid="rule-label-error">{{ e }}</mat-error>
                          }
                        </mat-form-field>
                        <mat-form-field>
                          <mat-label>Document type</mat-label>
                          <input
                            matInput
                            formControlName="documentTypeLabel"
                            [matAutocomplete]="ruleTypes"
                            placeholder="None"
                          />
                          <mat-autocomplete #ruleTypes="matAutocomplete">
                            <mat-option value="">None</mat-option>
                            @for (t of options(documentTypes(), v.documentTypeLabel); track t) {
                              <mat-option [value]="t">{{ t }}</mat-option>
                            }
                          </mat-autocomplete>
                          @if (error(rule.controls.documentTypeLabel); as e) {
                            <mat-error>{{ e }}</mat-error>
                          }
                        </mat-form-field>
                        <mat-form-field>
                          <mat-label>Mail type</mat-label>
                          <mat-select formControlName="mailType">
                            <mat-option value="">None</mat-option>
                            @for (t of mailTypes; track t) {
                              <mat-option [value]="t">{{ label(t) }}</mat-option>
                            }
                          </mat-select>
                        </mat-form-field>
                        <mat-form-field>
                          <mat-label>Retention days</mat-label>
                          <input matInput type="number" min="1" formControlName="retentionDays" />
                          @if (error(rule.controls.retentionDays); as e) {
                            <mat-error>{{ e }}</mat-error>
                          }
                        </mat-form-field>
                        <mat-form-field>
                          <mat-label>Action</mat-label>
                          <mat-select formControlName="action" data-testid="rule-action">
                            @for (a of actions; track a) {
                              <mat-option [value]="a">{{ a }}</mat-option>
                            }
                          </mat-select>
                          @if (error(rule.controls.action); as e) {
                            <mat-error>{{ e }}</mat-error>
                          }
                        </mat-form-field>
                      </div>
                      <fieldset class="m-0 border-0 p-0" [formGroup]="rule.controls.match">
                        <legend class="text-sm font-medium">
                          Match (all set fields must match)
                        </legend>
                        <div class="grid grid-cols-1 gap-x-4 md:grid-cols-3">
                          <mat-form-field>
                            <mat-label>Subject contains</mat-label>
                            <input
                              matInput
                              formControlName="subjectContains"
                              data-testid="rule-subject-contains"
                            />
                          </mat-form-field>
                          <mat-form-field>
                            <mat-label>Subject template</mat-label>
                            <input matInput formControlName="subjectTemplate" />
                          </mat-form-field>
                          <mat-form-field>
                            <mat-label>From address</mat-label>
                            <input matInput formControlName="fromAddress" />
                          </mat-form-field>
                          <mat-form-field>
                            <mat-label>From subdomain</mat-label>
                            <input matInput formControlName="fromSubdomain" />
                          </mat-form-field>
                          <mat-form-field>
                            <mat-label>Category</mat-label>
                            <mat-select formControlName="category">
                              <mat-option value="">Any</mat-option>
                              @for (c of categories; track c) {
                                <mat-option [value]="c">{{ c }}</mat-option>
                              }
                            </mat-select>
                          </mat-form-field>
                          <mat-form-field>
                            <mat-label>List-Id</mat-label>
                            <mat-select formControlName="listIdPresent">
                              <mat-option value="">Any</mat-option>
                              <mat-option value="yes">Present</mat-option>
                              <mat-option value="no">Absent</mat-option>
                            </mat-select>
                          </mat-form-field>
                          <mat-form-field>
                            <mat-label>Unsubscribe link</mat-label>
                            <mat-select formControlName="listUnsubscribePresent">
                              <mat-option value="">Any</mat-option>
                              <mat-option value="yes">Present</mat-option>
                              <mat-option value="no">Absent</mat-option>
                            </mat-select>
                          </mat-form-field>
                        </div>
                        @if (error(rule.controls.match); as e) {
                          <p class="error m-0 text-sm" role="alert" data-testid="rule-match-error">
                            {{ e }}
                          </p>
                        }
                      </fieldset>
                    </td>
                  </tr>
                }
              }
            </tbody>
          </table>
        </div>
      }
    </div>
  `,
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
