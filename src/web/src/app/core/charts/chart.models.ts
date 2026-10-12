/** A chart's data as a table: the text alternative a "Show table" toggle swaps in. */
export interface ChartTable {
  columns: string[];
  rows: (string | number)[][];
}
