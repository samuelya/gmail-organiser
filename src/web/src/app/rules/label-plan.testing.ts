import { LabelPlanItemDto } from './label-plan.models';

/** Synthetic plan item for specs; kept out of *.spec.ts so importing it does not pull in a suite. */
export const planItem = (over: Partial<LabelPlanItemDto> = {}): LabelPlanItemDto => ({
  id: 'i-1',
  kind: 'nest',
  labelId: 'L1',
  labelName: 'Topic-Alpha',
  messageCount: 12,
  proposedName: 'Topic/Alpha',
  targetLabelId: null,
  targetLabelName: null,
  affectedFilterIds: [],
  rationale: 'Synthetic rationale.',
  status: 'proposed',
  error: null,
  description: null,
  senderKeys: null,
  ...over,
});
