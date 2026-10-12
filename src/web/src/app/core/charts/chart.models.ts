import { Params } from '@angular/router';

/** A table cell that links where clicking its bar goes. */
export interface ChartLinkCell {
  text: string;
  link: string;
  queryParams?: Params;
}

/** A chart's data as a table: the text alternative a "Show table" toggle swaps in. */
export interface ChartTable {
  columns: string[];
  rows: (string | number | ChartLinkCell)[][];
}
