/** `GET /api/labels`: a Gmail label; `type` is `system` or `user`. */
export interface LabelDto {
  id: string;
  name: string;
  type: string;
}

/** One segment of the label tree; `path` is the full `/`-separated name. */
export interface LabelNode {
  name: string;
  path: string;
  /** False for a parent segment no Gmail label has on its own. */
  exists: boolean;
  children: LabelNode[];
}

/** Where a typed label path lands: under its deepest existing ancestor, with the segments to create. */
export interface LabelPlacement {
  exists: boolean;
  parent: string | null;
  created: string[];
}
