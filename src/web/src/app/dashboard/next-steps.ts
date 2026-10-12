import { AnalysisSummaryDto } from '../analyse/analysis.models';
import { FetchStatusDto } from './fetch.models';

/** One line of the Dashboard's "Next steps" card; `link` is `null` when the action is on the page. */
export interface NextStep {
  id: string;
  icon: string;
  text: string;
  link: string | null;
  queryParams?: Record<string, string>;
  linkLabel?: string;
}

/** What the steps are computed from; `null` while unknown (loading or failed). */
export interface NextStepsInput {
  gmailConnected: boolean | null;
  status: FetchStatusDto | null;
  summary: AnalysisSummaryDto | null;
}

export const MAX_NEXT_STEPS = 3;

/**
 * The first matching steps in priority order, at most three. An account mismatch adds no step (the
 * banner explains it) and suppresses the fetch step and "caught up"; so does a missing input, and
 * a first fetch still running suppresses "caught up".
 */
export function nextSteps({ gmailConnected, status, summary }: NextStepsInput): NextStep[] {
  const steps: NextStep[] = [];
  const mismatch = status?.accountMismatch ?? false;
  if (gmailConnected === false) {
    steps.push({
      id: 'connect',
      icon: 'link_off',
      text: 'Connect Gmail to start.',
      link: '/settings',
      linkLabel: 'Settings',
    });
  }
  if (
    status &&
    !mismatch &&
    gmailConnected !== false &&
    status.mailboxPhase === 'not_started' &&
    status.activeJob === null
  ) {
    steps.push({
      id: 'fetch',
      icon: 'download',
      text: 'Fetch your mailbox: use Start fetch below.',
      link: null,
    });
  }
  if (summary) {
    if (summary.analysed > 0) {
      steps.push({
        id: 'review',
        icon: 'fact_check',
        text: `${count(summary.analysed, 'suggestion', 'suggestions')} waiting for review.`,
        link: '/review',
        queryParams: { status: 'pending' },
        linkLabel: 'Review',
      });
    }
    if (summary.approved > 0) {
      steps.push({
        id: 'apply',
        icon: 'done_all',
        text: `${count(summary.approved, 'approved suggestion', 'approved suggestions')} not applied to Gmail yet.`,
        link: '/review',
        queryParams: { status: 'approved' },
        linkLabel: 'Apply',
      });
    }
    if (summary.toBeDeletedCount > 0) {
      steps.push({
        id: 'clean-up',
        icon: 'delete',
        text: `${count(summary.toBeDeletedCount, 'message', 'messages')} marked for deletion.`,
        link: '/clean-up',
        linkLabel: 'Clean-up',
      });
    }
    if (summary.notAnalysed > 0 && summary.analysed === 0) {
      steps.push({
        id: 'analyse',
        icon: 'psychology',
        text: `${count(summary.notAnalysed, 'message', 'messages')} not analysed yet.`,
        link: '/analyse',
        linkLabel: 'Analyse',
      });
    }
  }
  if (
    steps.length === 0 &&
    status &&
    summary &&
    !mismatch &&
    status.mailboxPhase !== 'not_started'
  ) {
    steps.push({ id: 'caught-up', icon: 'task_alt', text: "You're all caught up.", link: null });
  }
  return steps.slice(0, MAX_NEXT_STEPS);
}

function count(n: number, one: string, many: string): string {
  return `${n.toLocaleString()} ${n === 1 ? one : many}`;
}
