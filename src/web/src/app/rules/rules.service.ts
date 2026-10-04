import { HttpClient, HttpContext, HttpParams } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { QUIET_STATUSES } from '../core/error.interceptor';
import { PagedDto } from '../core/paging.models';
import { LabelPlanApplyDto, LabelPlanDto, UpdatePlanItemRequest } from './label-plan.models';
import {
  FilterDto,
  FilterEdit,
  FilterListDto,
  FilterPreviewDto,
  FilterProposalDto,
  FilterRequest,
  FilterSyncResultDto,
  PROPOSALS_PAGE_SIZE,
} from './rules.models';

/** Gmail filters (the synced snapshot, delete / restore, proposals, preview, create) and the label plan. */
@Injectable({ providedIn: 'root' })
export class RulesService {
  private readonly http = inject(HttpClient);

  list(includeDeleted = false): Observable<FilterListDto> {
    const params = new HttpParams().set('includeDeleted', includeDeleted);
    return this.http.get<FilterListDto>('/api/rules/filters', { params });
  }

  /** `503` when Gmail is not connected or rate-limiting. */
  sync(): Observable<FilterSyncResultDto> {
    return this.http.post<FilterSyncResultDto>('/api/rules/filters/sync', null);
  }

  /** `409` when already deleted. */
  delete(id: string): Observable<void> {
    return this.http.delete<void>(`/api/rules/filters/${encodeURIComponent(id)}`);
  }

  /** Re-creates a deleted filter as a new one; `409` when one of its labels is gone. */
  restore(id: string): Observable<FilterDto> {
    return this.http.post<FilterDto>(`/api/rules/filters/${encodeURIComponent(id)}/restore`, null);
  }

  /** Most messages first. */
  proposals(page: number, pageSize = PROPOSALS_PAGE_SIZE): Observable<PagedDto<FilterProposalDto>> {
    const params = new HttpParams().set('page', page).set('pageSize', pageSize);
    return this.http.get<PagedDto<FilterProposalDto>>('/api/rules/filters/proposals', { params });
  }

  /** `400` ValidationProblem for an incomplete request; Gmail problems come back as warnings. */
  preview(request: FilterRequest): Observable<FilterPreviewDto> {
    return this.http.post<FilterPreviewDto>('/api/rules/filters/preview', request);
  }

  /** `409` at Gmail's limits or on Gmail's refusal, `503` when Gmail is unreachable. */
  create(request: FilterRequest): Observable<FilterDto> {
    return this.http.post<FilterDto>('/api/rules/filters', request);
  }

  /** The newest plan that is not discarded; `404` (no snackbar) when there is none. */
  latestPlan(): Observable<LabelPlanDto> {
    const context = new HttpContext().set(QUIET_STATUSES, [404]);
    return this.http.get<LabelPlanDto>(`${PLANS}/latest`, { context });
  }

  /** Builds a new draft and discards the previous one; `503` / `502` when Gmail is unavailable. */
  createPlan(): Observable<LabelPlanDto> {
    return this.http.post<LabelPlanDto>(PLANS, null);
  }

  /** `400` for an invalid name or target, `409` once the plan is not a draft. */
  updateItem(planId: string, itemId: string, request: UpdatePlanItemRequest): Observable<LabelPlanDto> {
    return this.http.patch<LabelPlanDto>(
      `${PLANS}/${encodeURIComponent(planId)}/items/${encodeURIComponent(itemId)}`,
      request,
    );
  }

  /** Starts the `label_plan_apply` job; `409` unless the plan is a draft with an accepted item. */
  applyPlan(planId: string): Observable<LabelPlanApplyDto> {
    return this.http.post<LabelPlanApplyDto>(`${PLANS}/${encodeURIComponent(planId)}/apply`, null);
  }

  /** `409` once the plan is being applied or applied. */
  discardPlan(planId: string): Observable<LabelPlanDto> {
    return this.http.post<LabelPlanDto>(`${PLANS}/${encodeURIComponent(planId)}/discard`, null);
  }
}

const PLANS = '/api/rules/labels/plans';

/**
 * The dialog's form value for a request: the first label is the label path, a second one that is
 * an archive-rule label fills the archive select. With no request, only `from` is filled.
 */
export function filterEdit(
  request: FilterRequest | null,
  from: string,
  archiveLabels: readonly string[],
): FilterEdit {
  const c = request?.criteria;
  const names = request?.action.addLabelNames ?? [];
  const archive = names.slice(1).find((n) => archiveLabels.includes(n)) ?? '';
  return {
    from: c?.from ?? from,
    to: c?.to ?? '',
    subject: c?.subject ?? '',
    hasAttachment: c?.hasAttachment === true,
    query: c?.query ?? '',
    negatedQuery: c?.negatedQuery ?? '',
    labelPath: names[0] ?? '',
    archiveLabel: archive,
    skipInbox: request?.action.skipInbox ?? false,
    markRead: request?.action.markRead ?? false,
  };
}

/** The API request for a form value: blank fields are unset, label names trimmed and de-duplicated. */
export function filterRequest(edit: FilterEdit): FilterRequest {
  const text = (v: string) => v.trim() || null;
  const names: string[] = [];
  for (const raw of [edit.labelPath, edit.archiveLabel]) {
    const name = raw.trim();
    if (name && !names.some((n) => n.toLowerCase() === name.toLowerCase())) names.push(name);
  }
  return {
    criteria: {
      from: text(edit.from),
      to: text(edit.to),
      subject: text(edit.subject),
      query: text(edit.query),
      negatedQuery: text(edit.negatedQuery),
      hasAttachment: edit.hasAttachment ? true : null,
      excludeChats: null,
      size: null,
      sizeComparison: null,
    },
    action: { addLabelNames: names, skipInbox: edit.skipInbox, markRead: edit.markRead },
  };
}

/** Why the API would refuse the request (no criterion, no action), or null when it can be previewed. */
export function filterRequestProblem(request: FilterRequest): string | null {
  const c = request.criteria;
  if (!c.from && !c.to && !c.subject && !c.query && !c.negatedQuery && !c.hasAttachment) {
    return 'Add at least one criterion.';
  }
  const a = request.action;
  if (a.addLabelNames.length === 0 && !a.skipInbox && !a.markRead) {
    return 'Add at least one action: a label, skip inbox or mark read.';
  }
  return null;
}
