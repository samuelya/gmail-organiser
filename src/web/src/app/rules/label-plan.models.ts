/** `LabelPlanStatus`: at most one plan is a draft; only a draft is edited, applied or discarded. */
export type LabelPlanStatus = 'draft' | 'applying' | 'applied' | 'discarded';

/** `LabelPlanItemKind`: delete an empty label, merge a near-duplicate, rename a flat `X-Y` to `X/Y`. */
export type LabelPlanItemKind = 'empty' | 'near_duplicate' | 'nest';

export type LabelPlanItemStatus = 'proposed' | 'accepted' | 'rejected' | 'applied' | 'failed';

/** `LabelPlanItemDto`: one proposal; `error` is set when applying it failed. */
export interface LabelPlanItemDto {
  id: string;
  kind: LabelPlanItemKind;
  labelId: string;
  labelName: string;
  messageCount: number;
  /** The new name of a `nest` item. */
  proposedName: string | null;
  /** The label a `near_duplicate` item merges into. */
  targetLabelId: string | null;
  targetLabelName: string | null;
  /** Active filters whose action adds or removes the label. */
  affectedFilterIds: string[];
  rationale: string;
  status: LabelPlanItemStatus;
  error: string | null;
}

/** `LabelPlanDto`: `jobId` is the `label_plan_apply` job once the plan was applied. */
export interface LabelPlanDto {
  id: string;
  status: LabelPlanStatus;
  createdAt: string;
  updatedAt: string;
  /** User labels the plan considered. */
  labelCount: number;
  warnings: string[];
  items: LabelPlanItemDto[];
  jobId: string | null;
}

/** `PATCH …/items/{itemId}`: null or missing fields stay unchanged. */
export interface UpdatePlanItemRequest {
  status?: 'accepted' | 'rejected';
  /** A valid label path, for a `nest` item. */
  proposedName?: string;
  /** Another user label, for a `near_duplicate` item. */
  targetLabelId?: string;
}

/** `LabelPlanApplyDto`: progress comes through the jobs hub. */
export interface LabelPlanApplyDto {
  jobId: string;
}

/** The job type of `POST …/apply`. */
export const LABEL_PLAN_APPLY_JOB = 'label_plan_apply';

/** The kinds in the order the list and the apply dialog show them. */
export const PLAN_KINDS: readonly LabelPlanItemKind[] = ['empty', 'near_duplicate', 'nest'];

export const KIND_TITLES: Readonly<Record<LabelPlanItemKind, string>> = {
  empty: 'Empty labels',
  near_duplicate: 'Near-duplicates',
  nest: 'Flat → nested',
};

/** Accepted items per kind, as the apply dialog lists them. */
export type KindCounts = Readonly<Record<LabelPlanItemKind, number>>;

export function acceptedCounts(items: readonly LabelPlanItemDto[]): KindCounts {
  const counts = { empty: 0, near_duplicate: 0, nest: 0 };
  for (const item of items) if (item.status === 'accepted') counts[item.kind]++;
  return counts;
}

/** The inline proposal text: "delete", "→ new name" or "→ into target". */
export function proposalText(item: LabelPlanItemDto): string {
  switch (item.kind) {
    case 'empty':
      return 'delete';
    case 'nest':
      return `→ ${item.proposedName ?? ''}`;
    case 'near_duplicate':
      return `→ into ${item.targetLabelName ?? item.targetLabelId ?? ''}`;
  }
}
