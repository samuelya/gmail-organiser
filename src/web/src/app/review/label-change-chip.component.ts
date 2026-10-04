import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { MatChipsModule } from '@angular/material/chips';
import { labelChangeText, ReviewGroupDto } from './review.models';

/** What applying a suggestion does to the message's labels ("Moves A → B"); nothing for `none`. */
@Component({
  selector: 'app-label-change-chip',
  imports: [MatChipsModule],
  template: `
    @if (text(); as t) {
      <mat-chip class="chip" [title]="t" [disableRipple]="true" data-testid="label-change">
        <span class="chip-text">{{ t }}</span>
      </mat-chip>
    }
  `,
  styles: `
    :host {
      display: contents;
    }
    .chip {
      max-width: 100%;
    }
    .chip-text {
      display: block;
      max-width: 24rem;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LabelChangeChip {
  readonly change =
    input.required<Pick<ReviewGroupDto, 'labelChange' | 'topicLabel' | 'replaceLabels'>>();
  readonly text = computed(() => labelChangeText(this.change()));
}
