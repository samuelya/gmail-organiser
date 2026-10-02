import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { AbstractControl, ValidationErrors, ValidatorFn } from '@angular/forms';
import { Observable, of, shareReplay, tap } from 'rxjs';
import { LabelDto, LabelNode, LabelPlacement } from './labels.models';

/** The API's `GmailLimits.LabelNameMaxLength`. */
export const LABEL_PATH_MAX_LENGTH = 225;
/** The API's `LabelPath` pattern: up to five segments of at most 100 chars, none blank or starting with whitespace. */
const LABEL_PATH_PATTERN = /^[^/\s][^/]{0,99}(\/[^/\s][^/]{0,99}){0,4}$/;
/** The API's `GmailLimits.ReservedLabelNames`: Gmail system labels a topic label must not be. */
const RESERVED_LABEL_NAMES = new Set(
  [
    'INBOX',
    'UNREAD',
    'STARRED',
    'IMPORTANT',
    'SENT',
    'DRAFT',
    'SPAM',
    'TRASH',
    'CHAT',
    'CATEGORY_PERSONAL',
    'CATEGORY_SOCIAL',
    'CATEGORY_PROMOTIONS',
    'CATEGORY_UPDATES',
    'CATEGORY_FORUMS',
    'Drafts',
    'Sent Mail',
    'All Mail',
    'Chats',
    'Scheduled',
    'Snoozed',
  ].map((n) => n.toLowerCase()),
);
// eslint-disable-next-line no-control-regex
const CONTROL_CHARS = /[\u0000-\u001f\u007f-\u009f]/;

/** Gmail labels for the label picker, cached until "Refresh labels"; tree building and path rules. */
@Injectable({ providedIn: 'root' })
export class LabelsService {
  private readonly http = inject(HttpClient);
  private cached: Observable<LabelDto[]> | null = null;

  labels(): Observable<LabelDto[]> {
    this.cached ??= this.http
      .get<LabelDto[]>('/api/labels')
      .pipe(tap({ error: () => (this.cached = null) }), shareReplay(1));
    return this.cached;
  }

  /** Reloads the labels from Gmail and caches the result. */
  refresh(): Observable<LabelDto[]> {
    return this.http.post<LabelDto[]>('/api/labels/refresh', null).pipe(
      tap((labels) => {
        this.cached = of(labels);
      }),
    );
  }
}

/** The user labels as a tree split on `/`, sorted by name at every level. */
export function buildLabelTree(labels: readonly LabelDto[]): LabelNode[] {
  const roots: LabelNode[] = [];
  const byPath = new Map<string, LabelNode>();
  for (const label of labels) {
    if (label.type !== 'user') continue;
    let siblings = roots;
    let path = '';
    const segments = label.name.split('/');
    segments.forEach((name, i) => {
      path = path ? `${path}/${name}` : name;
      let node = byPath.get(path);
      if (!node) {
        node = { name, path, exists: false, children: [] };
        byPath.set(path, node);
        siblings.push(node);
      }
      if (i === segments.length - 1) node.exists = true;
      siblings = node.children;
    });
  }
  sortTree(roots);
  return roots;
}

function sortTree(nodes: LabelNode[]): void {
  nodes.sort((a, b) => a.name.localeCompare(b.name));
  nodes.forEach((n) => sortTree(n.children));
}

/** The nodes whose path contains `text` (case-insensitive), with their ancestors; all nodes when blank. */
export function filterLabelTree(nodes: readonly LabelNode[], text: string): LabelNode[] {
  const needle = text.trim().toLowerCase();
  if (!needle) return [...nodes];
  const keep = (node: LabelNode): LabelNode | null => {
    if (node.path.toLowerCase().includes(needle)) return node;
    const children = node.children.map(keep).filter((c): c is LabelNode => !!c);
    return children.length ? { ...node, children } : null;
  };
  return nodes.map(keep).filter((n): n is LabelNode => !!n);
}

/** Why the API would refuse `path` as a topic label, or null when it is valid. */
export function labelPathError(path: string): string | null {
  const trimmed = path.trim();
  if (!trimmed) return 'Enter a label.';
  if (trimmed.length > LABEL_PATH_MAX_LENGTH) return `At most ${LABEL_PATH_MAX_LENGTH} characters.`;
  if (CONTROL_CHARS.test(trimmed)) return 'No control characters.';
  if (trimmed.split('/').length > 5) return 'At most five levels.';
  if (!LABEL_PATH_PATTERN.test(trimmed)) {
    return "Each '/'-separated part needs up to 100 characters and must not be blank or start with a space.";
  }
  if (RESERVED_LABEL_NAMES.has(trimmed.toLowerCase())) return 'That is a Gmail system label.';
  return null;
}

/** `labelPathError` as a form validator: `{ labelPath: message }`. */
export const labelPathValidator: ValidatorFn = (
  control: AbstractControl,
): ValidationErrors | null => {
  const error = labelPathError(String(control.value ?? ''));
  return error ? { labelPath: error } : null;
};

/** Where `path` lands in the tree: an existing label, or the segments created under the deepest existing ancestor. */
export function labelPlacement(path: string, labels: readonly LabelDto[]): LabelPlacement {
  const names = new Set(labels.filter((l) => l.type === 'user').map((l) => l.name.toLowerCase()));
  const segments = path.trim().split('/');
  let depth = segments.length;
  while (depth > 0 && !names.has(segments.slice(0, depth).join('/').toLowerCase())) depth--;
  return {
    exists: depth === segments.length,
    parent: depth > 0 ? segments.slice(0, depth).join('/') : null,
    created: segments.slice(depth),
  };
}
