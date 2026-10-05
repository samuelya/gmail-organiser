import { DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { GroupingPreviewDto } from './analysis.models';

/**
 * The live preview of a selection: emails → groups → estimated LLM calls, and the largest groups; for top senders,
 * the candidate senders and lists, one policy call each.
 */
@Component({
  selector: 'app-grouping-preview',
  imports: [DecimalPipe, MatIconModule, MatProgressBarModule],
  template: `
    <div class="flex flex-col gap-3" aria-live="polite" data-testid="preview">
      @if (loading()) {
        <mat-progress-bar mode="indeterminate" aria-label="Updating the preview" />
      }
      @if (preview(); as p) {
        @if (p.senders; as senders) {
          <p class="summary m-0" data-testid="preview-summary">
            {{ senders.length | number }} {{ senders.length === 1 ? 'sender' : 'senders' }} ·
            {{ p.messages | number }} {{ p.messages === 1 ? 'email' : 'emails' }} → ≈
            {{ p.estimatedLlmCalls | number }} LLM
            {{ p.estimatedLlmCalls === 1 ? 'call' : 'calls' }}
          </p>
          @if (senders.length > 0) {
            <h3 class="subtitle m-0">Candidates</h3>
            <ul class="m-0 flex list-none flex-col gap-1 p-0" data-testid="preview-senders">
              @for (c of senders; track c.scope + c.scopeKey) {
                <li class="flex items-baseline gap-3" data-testid="preview-sender">
                  <span class="min-w-0 flex-1">
                    <span class="block break-words">{{ c.displayName || c.scopeKey }}</span>
                    <span class="muted block break-all text-xs">
                      {{ c.scope === 'list' ? 'List: ' : '' }}{{ c.scopeKey }}
                    </span>
                  </span>
                  <span class="whitespace-nowrap">
                    {{ c.count | number }} {{ c.count === 1 ? 'email' : 'emails' }}
                  </span>
                </li>
              }
            </ul>
          } @else {
            <p class="muted m-0" data-testid="preview-empty">No senders to propose a policy for.</p>
          }
        } @else {
          <p class="summary m-0" data-testid="preview-summary">
            {{ p.messages | number }} {{ p.messages === 1 ? 'email' : 'emails' }} →
            {{ p.groups | number }} {{ p.groups === 1 ? 'group' : 'groups' }} → ≈
            {{ p.estimatedLlmCalls | number }} LLM
            {{ p.estimatedLlmCalls === 1 ? 'call' : 'calls' }}, ≈
            {{ p.estimatedDerived | number }} derived, ≈ {{ p.estimatedFromMemory | number }} from
            memory
          </p>
          @if (!p.embeddingsAvailable) {
            <p class="muted m-0 flex items-center gap-2" data-testid="no-embeddings">
              <mat-icon aria-hidden="true">info</mat-icon>
              Embeddings unavailable: grouping uses sender and subject only.
            </p>
          }
          @if (p.largestGroups.length > 0) {
            <h3 class="subtitle m-0">Largest groups</h3>
            <ul class="m-0 flex list-none flex-col gap-1 p-0" data-testid="preview-groups">
              @for (g of p.largestGroups; track g.key) {
                <li class="flex items-baseline gap-3" data-testid="preview-group">
                  <span class="min-w-0 flex-1">
                    <span class="block break-words">{{ g.display }}</span>
                    <span class="muted block break-all text-xs">{{ g.senderAddress }}</span>
                  </span>
                  <span class="whitespace-nowrap">
                    {{ g.size | number }} · {{ g.representatives | number }} to the LLM
                  </span>
                </li>
              }
            </ul>
          } @else if (p.messages === 0) {
            <p class="muted m-0" data-testid="preview-empty">Nothing to analyse in this scope.</p>
          }
        }
      } @else if (!loading()) {
        <p class="muted m-0">Choose a scope and count to see the preview.</p>
      }
    </div>
  `,
  styles: `
    .summary {
      font: var(--mat-sys-title-small);
    }
    .subtitle {
      font: var(--mat-sys-label-large);
    }
    .muted {
      color: var(--mat-sys-on-surface-variant);
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class GroupingPreview {
  readonly preview = input<GroupingPreviewDto | null>(null);
  readonly loading = input(false);
}
