/** `LabelPlanStatus`: at most one plan is a draft; only a draft is edited, applied or discarded. */
export type LabelPlanStatus = 'draft' | 'applying' | 'applied' | 'discarded';

/**
 * `LabelPlanItemKind`: delete an empty label, merge a near-duplicate, rename a flat `X-Y` to `X/Y`,
 * create a taxonomy label (#366).
 */
export type LabelPlanItemKind = 'empty' | 'near_duplicate' | 'nest' | 'create';

export type LabelPlanItemStatus = 'proposed' | 'accepted' | 'rejected' | 'applied' | 'failed';

/** `LabelPlanItemDto`: one proposal; `error` is set when applying it failed. */
export interface LabelPlanItemDto {
  id: string;
  kind: LabelPlanItemKind;
  /** Empty for a taxonomy item whose label does not exist yet. */
  labelId: string;
  labelName: string;
  messageCount: number;
  /** The new name of a `nest` item, or the edited name of a `create` item. */
  proposedName: string | null;
  /** The label a `near_duplicate` item merges into (a taxonomy item: assigns its senders to). */
  targetLabelId: string | null;
  targetLabelName: string | null;
  /** Active filters whose action adds or removes the label. */
  affectedFilterIds: string[];
  rationale: string;
  status: LabelPlanItemStatus;
  error: string | null;
  /** A taxonomy item's description of the label. */
  description: string | null;
  /** A taxonomy item's canonical senders, which get proposed policies; null for the other items. */
  senderKeys: string[] | null;
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
  /** A valid label path, for a `nest` or `create` item. */
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

/** The job type of `POST /api/rules/labels/taxonomy`; it ends with a new draft plan. */
export const TAXONOMY_PROPOSE_JOB = 'taxonomy_propose';

/** The kinds in the order the list and the apply dialog show them. */
export const PLAN_KINDS: readonly LabelPlanItemKind[] = [
  'create',
  'empty',
  'near_duplicate',
  'nest',
];

export const KIND_TITLES: Readonly<Record<LabelPlanItemKind, string>> = {
  create: 'Taxonomy labels',
  empty: 'Empty labels',
  near_duplicate: 'Near-duplicates',
  nest: 'Flat → nested',
};

/** Accepted items per kind, as the apply dialog lists them. */
export type KindCounts = Readonly<Record<LabelPlanItemKind, number>>;

export function acceptedCounts(items: readonly LabelPlanItemDto[]): KindCounts {
  const counts = { create: 0, empty: 0, near_duplicate: 0, nest: 0 };
  for (const item of items) if (item.status === 'accepted') counts[item.kind]++;
  return counts;
}

/** A taxonomy proposal (#366): its senders get proposed policies for the label. */
export function isTaxonomyItem(item: LabelPlanItemDto): boolean {
  return item.senderKeys !== null;
}

/** The label a `create` item makes, or the existing one its senders go to. */
export function createName(item: LabelPlanItemDto): string {
  return item.proposedName ?? item.labelName;
}

/**
 * The inline proposal text: "delete", "→ new name", "→ into target", "create …" or, for a taxonomy
 * near-duplicate, "→ senders into target".
 */
export function proposalText(item: LabelPlanItemDto): string {
  switch (item.kind) {
    case 'create':
      if (item.labelId) return 'existing label: senders get a policy';
      return item.proposedName ? `create as ${item.proposedName}` : 'create';
    case 'empty':
      return 'delete';
    case 'nest':
      return `→ ${item.proposedName ?? ''}`;
    case 'near_duplicate': {
      const target = item.targetLabelName ?? item.targetLabelId ?? '';
      return isTaxonomyItem(item) ? `→ senders into ${target}` : `→ into ${target}`;
    }
  }
}
