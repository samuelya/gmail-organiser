import {
  afterRenderEffect,
  ChangeDetectionStrategy,
  Component,
  computed,
  input,
  output,
  signal,
  viewChild,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatTree, MatTreeModule } from '@angular/material/tree';
import { LabelDto, LabelNode } from './labels.models';
import { buildLabelTree, filterLabelTree } from './labels.service';

/** The user's Gmail labels as an expandable tree with a filter box; picking a node emits its full path. */
@Component({
  selector: 'app-label-tree-picker',
  imports: [MatButtonModule, MatFormFieldModule, MatIconModule, MatInputModule, MatTreeModule],
  template: `
    <mat-form-field class="w-full" subscriptSizing="dynamic">
      <mat-label>Filter labels</mat-label>
      <input
        matInput
        type="search"
        [value]="filter()"
        (input)="filter.set($any($event.target).value)"
        data-testid="label-filter"
      />
    </mat-form-field>
    @if (visible().length) {
      <div class="tree-box mt-2 max-h-56 overflow-auto rounded">
        <mat-tree
          #tree
          [dataSource]="visible()"
          [childrenAccessor]="children"
          [expansionKey]="key"
          aria-label="Existing labels"
        >
          <mat-tree-node
            *matTreeNodeDef="let node"
            matTreeNodePadding
            [isExpandable]="node.children.length > 0"
            (click)="pick.emit(node.path)"
            (activation)="pick.emit(node.path)"
            [class.selected]="node.path === selected()"
            [attr.aria-selected]="node.path === selected()"
            class="cursor-pointer"
            data-testid="label-node"
            [attr.data-path]="node.path"
          >
            @if (node.children.length) {
              <button
                mat-icon-button
                type="button"
                matTreeNodeToggle
                tabindex="-1"
                [attr.aria-label]="'Toggle ' + node.path"
                (click)="$event.stopPropagation()"
              >
                <mat-icon aria-hidden="true">{{
                  tree.isExpanded(node) ? 'expand_more' : 'chevron_right'
                }}</mat-icon>
              </button>
            } @else {
              <span class="inline-block w-10" aria-hidden="true"></span>
            }
            <span [class.muted]="!node.exists">{{ node.name }}</span>
          </mat-tree-node>
        </mat-tree>
      </div>
    } @else {
      <p class="muted m-0 mt-2 text-sm" data-testid="label-tree-empty">
        {{ filter().trim() ? 'No label matches the filter.' : 'No labels yet.' }}
      </p>
    }
  `,
  styles: `
    .tree-box {
      border: 1px solid var(--mat-sys-outline-variant);
    }
    mat-tree-node {
      min-height: 2.5rem;
    }
    mat-tree-node.selected {
      background: var(--mat-sys-secondary-container);
      color: var(--mat-sys-on-secondary-container);
    }
    .muted {
      color: var(--mat-sys-on-surface-variant);
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LabelTreePicker {
  readonly labels = input.required<readonly LabelDto[]>();
  /** The path currently in the label field; its node is highlighted and its ancestors start expanded. */
  readonly selected = input('');
  readonly pick = output<string>();

  readonly filter = signal('');
  private readonly tree = computed(() => buildLabelTree(this.labels()));
  readonly visible = computed(() => filterLabelTree(this.tree(), this.filter()));
  private readonly matTree = viewChild<MatTree<LabelNode, string>>('tree');
  private revealed = false;

  readonly children = (node: LabelNode) => node.children;
  readonly key = (node: LabelNode) => node.path;

  constructor() {
    afterRenderEffect(() => {
      const tree = this.matTree();
      const nodes = this.visible();
      if (!tree) return;
      if (this.filter().trim()) {
        tree.expandAll();
      } else if (!this.revealed) {
        this.revealed = true;
        expandAncestors(tree, nodes, this.selected());
      }
    });
  }
}

/** Expands the nodes above `path` so the current label is visible. */
function expandAncestors(
  tree: MatTree<LabelNode, string>,
  nodes: readonly LabelNode[],
  path: string,
): void {
  let level = nodes;
  for (const segment of path.split('/').slice(0, -1)) {
    const node = level.find((n) => n.name === segment);
    if (!node) return;
    tree.expand(node);
    level = node.children;
  }
}
