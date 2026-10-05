import { HttpClient, HttpContext, HttpParams } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { map, Observable } from 'rxjs';
import { QUIET_STATUSES } from '../core/error.interceptor';
import { JobDto } from '../core/jobs.models';
import { PagedDto } from '../core/paging.models';
import { StartFetchResponse } from '../dashboard/fetch.models';
import {
  ArchiveSendersRequest,
  NoisyProposalRequest,
  NoisyProposalResult,
  NoisyQuery,
  NoisySenderDto,
  SenderDto,
  SenderFetchStarted,
  SenderQuery,
} from './senders.models';

/** A refused Stage-0 action (422) is shown on the page with its reason, not in a snackbar. */
const STAGE0_QUIET = () => new HttpContext().set(QUIET_STATUSES, [422]);

/** Senders list and sender fetch. Pause, resume and cancel are the generic job endpoints on `JobsService`. */
@Injectable({ providedIn: 'root' })
export class SendersService {
  private readonly http = inject(HttpClient);

  /** `allowlisted` filters on the flag; left out lists every sender. */
  list(query: SenderQuery, allowlisted?: boolean): Observable<PagedDto<SenderDto>> {
    let params = new HttpParams()
      .set('page', query.page)
      .set('pageSize', query.pageSize)
      .set('sort', query.sort)
      .set('dir', query.dir);
    const search = query.search.trim();
    if (search) params = params.set('search', search);
    for (const kind of query.kinds) params = params.append('kind', kind);
    if (allowlisted !== undefined) params = params.set('allowlisted', allowlisted);
    return this.http.get<PagedDto<SenderDto>>('/api/senders', { params });
  }

  /** Noisy senders by canonical address, highest volume first. */
  listNoisy(query: NoisyQuery): Observable<PagedDto<NoisySenderDto>> {
    let params = new HttpParams()
      .set('minMessages', query.minMessages)
      .set('minUnreadRatio', query.minUnreadPercent / 100)
      .set('page', query.page)
      .set('pageSize', query.pageSize);
    if (query.dormantDays !== null) params = params.set('dormantDays', query.dormantDays);
    const search = query.search.trim();
    if (search) params = params.set('search', search);
    return this.http.get<PagedDto<NoisySenderDto>>('/api/senders/noisy', { params });
  }

  /** Creates pending To-Be-Deleted suggestions for the senders' mail; 422 names a refused sender. */
  proposeNoisy(request: NoisyProposalRequest): Observable<NoisyProposalResult> {
    return this.http.post<NoisyProposalResult>('/api/senders/noisy/proposals', request, {
      context: STAGE0_QUIET(),
    });
  }

  /** Queues the archive of the senders' inbox mail (202 with the job); 422 names a refused sender. */
  archiveSenders(request: ArchiveSendersRequest): Observable<JobDto> {
    return this.http.post<JobDto>('/api/senders/archive', request, { context: STAGE0_QUIET() });
  }

  /** Sets an address's allowlist flag; allowlisting an address never fetched adds it with no mail. */
  setAllowlisted(address: string, allowlisted: boolean): Observable<SenderDto> {
    return this.http.put<SenderDto>(`/api/senders/${encodeURIComponent(address)}/allowlist`, {
      allowlisted,
    });
  }

  /**
   * Fetches all mail from an address or a domain: `202` for a new job, `200` when that target's job is
   * already active or was resumed. Other targets queue behind the running fetch.
   */
  fetchFromSender(target: string): Observable<SenderFetchStarted> {
    return this.http
      .post<StartFetchResponse>('/api/fetch/sender', { target }, { observe: 'response' })
      .pipe(
        map((response) => ({
          jobId: response.body?.jobId ?? '',
          created: response.status === 202,
        })),
      );
  }
}
