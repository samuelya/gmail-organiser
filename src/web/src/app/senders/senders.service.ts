import { HttpClient, HttpParams } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { map, Observable } from 'rxjs';
import { PagedDto } from '../core/paging.models';
import { StartFetchResponse } from '../dashboard/fetch.models';
import { SenderDto, SenderFetchStarted, SenderQuery } from './senders.models';

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
    if (allowlisted !== undefined) params = params.set('allowlisted', allowlisted);
    return this.http.get<PagedDto<SenderDto>>('/api/senders', { params });
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
