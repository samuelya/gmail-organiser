import { HttpClient, HttpParams } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { AnalysisRunDto } from '../analyse/analysis.models';
import { JobDto } from '../core/jobs.models';
import { PagedDto } from '../core/paging.models';
import {
  ActionBatchDto,
  ApplyRestResponse,
  BulkApproveRequest,
  BulkApproveResponse,
  GroupDecisionResponse,
  ReviewOutcome,
  ReviewSenderDetailDto,
  ReviewSenderDto,
  ReviewStatus,
  SenderPatternDto,
  SuggestionDto,
} from './review.models';

/** Review by sender and group, apply approved suggestions and apply to the rest of a sender. */
@Injectable({ providedIn: 'root' })
export class ReviewService {
  private readonly http = inject(HttpClient);

  listSenders(
    status: ReviewStatus,
    search: string,
    page: number,
    pageSize: number,
  ): Observable<PagedDto<ReviewSenderDto>> {
    let params = new HttpParams().set('status', status).set('page', page).set('pageSize', pageSize);
    const trimmed = search.trim();
    if (trimmed) params = params.set('search', trimmed);
    return this.http.get<PagedDto<ReviewSenderDto>>('/api/review/senders', { params });
  }

  sender(
    address: string,
    status: ReviewStatus,
    page: number,
    pageSize: number,
  ): Observable<ReviewSenderDetailDto> {
    const params = new HttpParams()
      .set('status', status)
      .set('page', page)
      .set('pageSize', pageSize);
    return this.http.get<ReviewSenderDetailDto>(
      `/api/review/senders/${encodeURIComponent(address)}`,
      {
        params,
      },
    );
  }

  approve(id: string): Observable<SuggestionDto> {
    return this.http.post<SuggestionDto>(
      `/api/review/suggestions/${encodeURIComponent(id)}/approve`,
      null,
    );
  }

  reject(id: string): Observable<SuggestionDto> {
    return this.http.post<SuggestionDto>(
      `/api/review/suggestions/${encodeURIComponent(id)}/reject`,
      null,
    );
  }

  /** Saves the edited outcome and approves the suggestion; `400` with field errors on an invalid label. */
  edit(id: string, outcome: ReviewOutcome): Observable<SuggestionDto> {
    return this.http.put<SuggestionDto>(
      `/api/review/suggestions/${encodeURIComponent(id)}`,
      outcome,
    );
  }

  /** Approves the group's pending members whose outcome is the card's. */
  approveGroup(
    senderAddress: string,
    groupKey: string,
    outcome: ReviewOutcome,
  ): Observable<GroupDecisionResponse> {
    return this.http.post<GroupDecisionResponse>('/api/review/groups/approve', {
      senderAddress,
      groupKey,
      ...outcome,
    });
  }

  rejectGroup(senderAddress: string, groupKey: string): Observable<GroupDecisionResponse> {
    return this.http.post<GroupDecisionResponse>('/api/review/groups/reject', {
      senderAddress,
      groupKey,
    });
  }

  bulkApprove(request: BulkApproveRequest): Observable<BulkApproveResponse> {
    return this.http.post<BulkApproveResponse>('/api/review/bulk-approve', request);
  }

  analyseIndividually(suggestionIds: string[]): Observable<AnalysisRunDto> {
    return this.http.post<AnalysisRunDto>('/api/review/analyse-individually', { suggestionIds });
  }

  /** `202` with the batch and its job; `409` when nothing matching is approved. */
  apply(senderAddress: string): Observable<ActionBatchDto> {
    return this.http.post<ActionBatchDto>('/api/review/apply', { senderAddress });
  }

  pattern(address: string): Observable<SenderPatternDto> {
    return this.http.get<SenderPatternDto>(
      `/api/review/senders/${encodeURIComponent(address)}/pattern`,
    );
  }

  /** Applies the sender's pattern to its messages without a suggestion (the body may override it). */
  applyRest(address: string): Observable<ApplyRestResponse> {
    return this.http.post<ApplyRestResponse>(
      `/api/review/senders/${encodeURIComponent(address)}/apply-rest`,
      {},
    );
  }

  /** A job's current state, for when the hub no longer holds it (it finished while disconnected). */
  job(id: string): Observable<JobDto> {
    return this.http.get<JobDto>(`/api/jobs/${encodeURIComponent(id)}`);
  }
}
