import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { FetchStatusDto, StartFetchResponse } from './fetch.models';

/** Fetch API. Pause, resume and cancel are the generic job endpoints on `JobsService`. */
@Injectable({ providedIn: 'root' })
export class FetchService {
  private readonly http = inject(HttpClient);

  getStatus(): Observable<FetchStatusDto> {
    return this.http.get<FetchStatusDto>('/api/fetch/status');
  }

  /** Starts the fetch, or resumes the latest failed or paused one; answers with its job id. */
  startMailboxFetch(): Observable<StartFetchResponse> {
    return this.http.post<StartFetchResponse>('/api/fetch/mailbox/start', null);
  }

  /** Starts the label resync of stored mail, or answers with the one already queued or running. */
  resyncLabels(): Observable<StartFetchResponse> {
    return this.http.post<StartFetchResponse>('/api/fetch/labels/resync', null);
  }
}
