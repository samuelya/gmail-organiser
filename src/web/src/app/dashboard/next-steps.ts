import { AnalysisSummaryDto, countText } from '../analyse/analysis.models';
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
 * The first matching steps in priority order, at most three. An account mismatch ends the list: the
 * banner explains it, and the local counts belong to another account. The fetch step needs Gmail known
 * to be connected; a missing input suppresses "caught up", and so does a first fetch not finished yet.
 */
export function nextSteps({ gmailConnected, status, summary }: NextStepsInput): NextStep[] {
  const steps: NextStep[] = [];
  if (gmailConnected === false) {
    steps.push({
      id: 'connect',
      icon: 'link_off',
      text: 'Connect Gmail to start.',
      link: '/settings',
      linkLabel: 'Settings',
    });
  }
  if (status?.accountMismatch) return steps;
  const firstFetch = gmailConnected === true && status?.mailboxPhase === 'not_started';
  const firstJob = firstFetch ? (status.activeJob?.status ?? null) : null;
  if (firstFetch && status.activeJob === null) {
    steps.push({
      id: 'fetch',
      icon: 'download',
      text: 'Fetch your mailbox: use Start fetch below.',
      link: null,
    });
  } else if (firstJob === 'paused') {
    steps.push({
      id: 'fetch',
      icon: 'play_arrow',
      text: 'Your first fetch is paused: use Resume below.',
      link: null,
    });
  } else if (firstJob === 'queued') {
    steps.push({
      id: 'fetch',
      icon: 'schedule',
      text: 'Your first fetch is queued and starts shortly.',
      link: null,
    });
  }
  if (summary) {
    if (summary.analysed > 0) {
      steps.push({
        id: 'review',
        icon: 'fact_check',
        text: `${countText(summary.analysed, 'suggestion', 'suggestions')} waiting for review.`,
        link: '/review',
        queryParams: { status: 'pending' },
        linkLabel: 'Review',
      });
    }
    if (summary.approved > 0) {
      steps.push({
        id: 'apply',
        icon: 'done_all',
        text: `${countText(summary.approved, 'approved suggestion', 'approved suggestions')} not applied to Gmail yet.`,
        link: '/review',
        queryParams: { status: 'approved' },
        linkLabel: 'Apply',
      });
    }
    if (summary.toBeDeletedCount > 0) {
      steps.push({
        id: 'clean-up',
        icon: 'delete',
        text: `${countText(summary.toBeDeletedCount, 'message', 'messages')} marked for deletion.`,
        link: '/clean-up',
        linkLabel: 'Clean-up',
      });
    }
    if (summary.notAnalysed > 0 && summary.analysed === 0) {
      steps.push({
        id: 'analyse',
        icon: 'psychology',
        text: `${countText(summary.notAnalysed, 'message', 'messages')} not analysed yet.`,
        link: '/analyse',
        linkLabel: 'Analyse',
      });
    }
  }
  if (steps.length === 0 && status && summary && status.mailboxPhase !== 'not_started') {
    steps.push({ id: 'caught-up', icon: 'task_alt', text: "You're all caught up.", link: null });
  }
  return steps.slice(0, MAX_NEXT_STEPS);
}
