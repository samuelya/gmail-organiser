import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { LabelDto, LabelNode } from '../review/labels.models';
import { buildLabelTree } from '../review/labels.service';
import { LabelPlanItemDto, proposalText } from './label-plan.models';

interface TreeRow {
  node: LabelNode;
  depth: number;
  item: LabelPlanItemDto | undefined;
}

/** The current label tree, each label with a plan item highlighted and its proposal inline. */
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
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LabelPlanTree {
  readonly labels = input.required<readonly LabelDto[]>();
  readonly items = input.required<readonly LabelPlanItemDto[]>();

  readonly proposal = proposalText;

  /** The tree flattened depth-first; an item matches its label by name, as the plan saw it. */
  readonly rows = computed(() => {
    const byName = new Map(this.items().map((i) => [i.labelName, i]));
    const rows: TreeRow[] = [];
    const walk = (nodes: readonly LabelNode[], depth: number) => {
      for (const node of nodes) {
        rows.push({ node, depth, item: node.exists ? byName.get(node.path) : undefined });
        walk(node.children, depth + 1);
      }
    };
    walk(buildLabelTree(this.labels()), 0);
    return rows;
  });
}
