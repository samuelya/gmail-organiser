/** `PagedDto<T>`: one page of a server-side paged list; `total` counts every matching item. */
export interface PagedDto<T> {
  items: T[];
  page: number;
  pageSize: number;
  total: number;
}

export type SortDirection = 'asc' | 'desc';
