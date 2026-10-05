import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { LabelDto, LabelNode } from '../review/labels.models';
import { buildLabelTree } from '../review/labels.service';
import { createName, LabelPlanItemDto, proposalText } from './label-plan.models';

interface TreeRow {
  node: LabelNode;
  depth: number;
  item: LabelPlanItemDto | undefined;
  /** A label a `create` item would make. */
  isNew: boolean;
}

/**
 * The current label tree, each label with a plan item highlighted and its proposal inline; labels a
 * taxonomy item would create are shown where they would land.
 */
@Component({
  selector: 'app-label-plan-tree',
  template: `
    @if (rows().length) {
      <ul class="m-0 list-none p-0" aria-label="Label tree with the proposed changes">
        @for (row of rows(); track row.node.path) {
          <li
            class="row flex flex-wrap items-baseline gap-x-2 rounded py-1 pr-2"
            [style.padding-left.rem]="0.5 + row.depth * 1.25"
            [class.proposed]="!!row.item"
            [class.rejected]="row.item?.status === 'rejected'"
            [class.new]="row.isNew"
            data-testid="plan-tree-row"
            [attr.data-path]="row.node.path"
          >
            <span
              [class.muted]="!row.node.exists"
              [class.line-through]="row.item?.kind === 'empty'"
              >{{ row.node.name }}</span
            >
            @if (row.item; as item) {
              <span class="text-sm" data-testid="plan-tree-proposal">{{ proposal(item) }}</span>
              @if (item.status !== 'proposed') {
                <span class="muted text-xs">({{ item.status }})</span>
              }
            }
          </li>
        }
      </ul>
    } @else {
      <p class="muted m-0" data-testid="plan-tree-empty">No user labels.</p>
    }
  `,
  styles: `
    .muted {
      color: var(--mat-sys-on-surface-variant);
    }
    li.proposed {
      background: var(--mat-sys-secondary-container);
      color: var(--mat-sys-on-secondary-container);
    }
    li.rejected {
      opacity: 0.6;
    }
    li.new span:first-child {
      font-style: italic;
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LabelPlanTree {
  readonly labels = input.required<readonly LabelDto[]>();
  readonly items = input.required<readonly LabelPlanItemDto[]>();

  readonly proposal = proposalText;

  /**
   * The tree flattened depth-first; an item matches its label by name, as the plan saw it. A
   * taxonomy near-duplicate (no label id) names no label of its own, so the list view shows it.
   */
  readonly rows = computed(() => {
    const labels = this.labels();
    const existing = new Set(labels.filter((l) => l.type === 'user').map((l) => l.name));
    const created = this.items().filter((i) => i.kind === 'create' && !i.labelId);
    const newPaths = new Set(created.map(createName).filter((name) => !existing.has(name)));
    const byName = new Map<string, LabelPlanItemDto>();
    for (const item of this.items()) {
      if (item.labelId) byName.set(item.labelName, item);
    }
    for (const item of created) byName.set(createName(item), item);
    const rows: TreeRow[] = [];
    const walk = (nodes: readonly LabelNode[], depth: number) => {
      for (const node of nodes) {
        const item = node.exists ? byName.get(node.path) : undefined;
        rows.push({ node, depth, item, isNew: newPaths.has(node.path) });
        walk(node.children, depth + 1);
      }
    };
    const proposed: LabelDto[] = [...newPaths].map((name) => ({ id: '', name, type: 'user' }));
    walk(buildLabelTree([...labels, ...proposed]), 0);
    return rows;
  });
}
