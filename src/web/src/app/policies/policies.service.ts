import { HttpClient, HttpContext, HttpParams } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { forkJoin, map, Observable } from 'rxjs';
import { QUIET_STATUSES } from '../core/error.interceptor';
import { PagedDto } from '../core/paging.models';
import {
  ApprovePolicyResponse,
  EditPolicyRequest,
  EditPolicyResponse,
  POLICY_STATUSES,
  PolicyQuery,
  PolicyStatus,
  SenderPolicyDetailDto,
  SenderPolicyDto,
} from './policies.models';

/** A refused apply (422, with its reason) and field errors (400) are shown on the page, not in a snackbar. */
const ON_PAGE = (...statuses: number[]) => new HttpContext().set(QUIET_STATUSES, statuses);

/** Sender policies (#358) and their approve / apply jobs (#359). Job progress is on `JobsService`. */
@Injectable({ providedIn: 'root' })
export class PoliciesService {
  private readonly http = inject(HttpClient);

  list(query: PolicyQuery): Observable<PagedDto<SenderPolicyDto>> {
    let params = new HttpParams()
      .set('status', query.status)
      .set('page', query.page)
      .set('pageSize', query.pageSize);
    const search = query.search.trim();
    if (search) params = params.set('search', search);
    return this.http.get<PagedDto<SenderPolicyDto>>('/api/policies', { params });
  }

  /** The number of policies per status tab for `search`: one single-item page each. */
  counts(search: string): Observable<Record<PolicyStatus, number>> {
    return forkJoin(
      POLICY_STATUSES.map((status) =>
        this.list({ status, search, page: 1, pageSize: 1 }).pipe(map((p) => p.total)),
      ),
    ).pipe(map((totals) => ({ proposed: totals[0], approved: totals[1], rejected: totals[2] })));
  }

  get(id: string): Observable<SenderPolicyDetailDto> {
    return this.http.get<SenderPolicyDetailDto>(`/api/policies/${encodeURIComponent(id)}`);
  }

  edit(id: string, request: EditPolicyRequest): Observable<EditPolicyResponse> {
    return this.http.put<EditPolicyResponse>(`/api/policies/${encodeURIComponent(id)}`, request, {
      context: ON_PAGE(400),
    });
  }

  approve(id: string): Observable<ApprovePolicyResponse> {
    return this.http.post<ApprovePolicyResponse>(
      `/api/policies/${encodeURIComponent(id)}/approve`,
      null,
      { context: ON_PAGE(422) },
    );
  }

  /** "Apply again": re-applies an approved policy; 409 while its job is queued or running. */
  apply(id: string): Observable<ApprovePolicyResponse> {
    return this.http.post<ApprovePolicyResponse>(
      `/api/policies/${encodeURIComponent(id)}/apply`,
      null,
      { context: ON_PAGE(422) },
    );
  }

  reject(id: string): Observable<SenderPolicyDto> {
    return this.http.post<SenderPolicyDto>(`/api/policies/${encodeURIComponent(id)}/reject`, null);
  }

  decideRule(
    id: string,
    ruleId: string,
    decision: 'approve' | 'reject',
  ): Observable<SenderPolicyDetailDto> {
    return this.http.post<SenderPolicyDetailDto>(
      `/api/policies/${encodeURIComponent(id)}/rules/${encodeURIComponent(ruleId)}/${decision}`,
      null,
    );
  }
}
