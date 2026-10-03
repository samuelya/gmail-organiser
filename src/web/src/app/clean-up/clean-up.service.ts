import { HttpClient, HttpParams } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { JobDto } from '../core/jobs.models';
import { PagedDto } from '../core/paging.models';
import {
  CleanupBatch,
  CleanupMessage,
  CleanupSelection,
  CleanupSender,
  CleanupSummary,
  selectionRequest,
} from './clean-up.models';

/** The delete-labelled mail by sender, removing it from the list, and Delete (to Trash). */
@Injectable({ providedIn: 'root' })
export class CleanUpService {
  private readonly http = inject(HttpClient);

  summary(): Observable<CleanupSummary> {
    return this.http.get<CleanupSummary>('/api/clean-up/summary');
  }

  senders(page: number, pageSize: number, search: string): Observable<PagedDto<CleanupSender>> {
    let params = new HttpParams().set('page', page).set('pageSize', pageSize);
    const trimmed = search.trim();
    if (trimmed) params = params.set('search', trimmed);
    return this.http.get<PagedDto<CleanupSender>>('/api/clean-up/senders', { params });
  }

  messages(address: string, page: number, pageSize: number): Observable<PagedDto<CleanupMessage>> {
    const params = new HttpParams().set('page', page).set('pageSize', pageSize);
    return this.http.get<PagedDto<CleanupMessage>>(
      `/api/clean-up/senders/${encodeURIComponent(address)}/messages`,
      { params },
    );
  }

  /** Removes the delete label; `null` (204) when no selected message still carries it. */
  unmark(selection: CleanupSelection): Observable<CleanupBatch | null> {
    return this.http.post<CleanupBatch | null>('/api/clean-up/unmark', selectionRequest(selection));
  }

  /** Moves to Trash, skipping protected messages unless included; `null` (204) when nothing qualifies. */
  delete(selection: CleanupSelection, includeProtected: boolean): Observable<CleanupBatch | null> {
    return this.http.post<CleanupBatch | null>(
      '/api/clean-up/delete',
      selectionRequest(selection, includeProtected),
    );
  }

  /** A job's current state, for when the hub no longer holds it (it finished while disconnected). */
  job(id: string): Observable<JobDto> {
    return this.http.get<JobDto>(`/api/jobs/${encodeURIComponent(id)}`);
  }
}
