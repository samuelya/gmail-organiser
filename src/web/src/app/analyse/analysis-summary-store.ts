import { inject, Injectable } from '@angular/core';
import { mailboxCounts } from '../dashboard/mailbox-counts';
import { AnalysisService } from './analysis.service';

/**
 * The Dashboard's one `GET /api/analysis/summary`, shared by every card that shows it. Provided by the
 * page, so it lives (and refetches) only as long as the page.
 */
@Injectable()
export class AnalysisSummaryStore {
  private readonly api = inject(AnalysisService);
  private readonly counts = mailboxCounts(() => this.api.summary());

  /** The last summary that loaded; kept after a failed reload. */
  readonly summary = this.counts.value;
  /** The latest request failed. */
  readonly loadFailed = this.counts.loadFailed;

  load(): void {
    this.counts.load();
  }
}
