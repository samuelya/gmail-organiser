import { humanise } from '../dashboard/fetch.models';

export const HISTORY_PAGE_SIZES = [25, 50, 100];
export const DEFAULT_HISTORY_PAGE_SIZE = 25;

/** `ActionBatchDto`: one History entry, a set of Gmail changes undone together. */
export interface ActionBatchDto {
  id: string;
  /** `apply`, `apply_rest`, `auto_archive`, `undo`, `trash`, `unmark`, `filter_labels`, `label_merge` or `label_plan` (snake_case). */
  kind: string;
  description: string;
  messageCount: number;
  /** For an undo batch: the batch it undoes. */
  undoOf: string | null;
  undoneAt: string | null;
  jobId: string | null;
  createdAt: string;
  /** The server's view; it still answers 409 while the batch's apply or another undo is unfinished. */
  canUndo: boolean;
}

/** `ActionLogRowDto`: one message's change; the names are the labels' current names (the id when unknown). */
export interface ActionLogRowDto {
  id: string;
  messageId: string;
  subject: string | null;
  labelsAdded: string[];
  labelsRemoved: string[];
  labelNamesAdded: string[];
  labelNamesRemoved: string[];
  note: string | null;
  undoneByBatchId: string | null;
}

/** `LabelDto`: a Gmail label. */
export interface LabelDto {
  id: string;
  name: string;
  type: string;
}

/** `ActionBatchDetailDto`: the batch with at most 500 of its rows; `truncated` when it has more. */
export interface ActionBatchDetailDto {
  batch: ActionBatchDto;
  rows: ActionLogRowDto[];
  truncated: boolean;
  /** Labels the batch created; undo keeps them. */
  createdLabels: LabelDto[];
}

const KIND_LABELS: Readonly<Record<string, string>> = {
  apply: 'Apply',
  apply_rest: 'Apply rest',
  auto_archive: 'Auto-archive',
  label_merge: 'Label merge',
  label_plan: 'Label plan',
  undo: 'Undo',
};

export function kindLabel(kind: string): string {
  return KIND_LABELS[kind] ?? humanise(kind);
}

export function undoConfirmMessage(batch: ActionBatchDto): string {
  const n = batch.messageCount;
  return `Restores the labels of ${n} ${n === 1 ? 'message' : 'messages'}; created labels stay.`;
}
