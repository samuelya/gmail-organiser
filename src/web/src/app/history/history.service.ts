import { HttpClient, HttpParams } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { PagedDto } from '../core/paging.models';
import { ActionBatchDetailDto, ActionBatchDto } from './history.models';

/** History batches, their log and undo. Undo progress comes live from `JobsService` by `jobId`. */
@Injectable({ providedIn: 'root' })
export class HistoryService {
  private readonly http = inject(HttpClient);

  /** Newest first. */
  list(page: number, pageSize: number): Observable<PagedDto<ActionBatchDto>> {
    const params = new HttpParams().set('page', page).set('pageSize', pageSize);
    return this.http.get<PagedDto<ActionBatchDto>>('/api/history', { params });
  }

  get(id: string): Observable<ActionBatchDetailDto> {
    return this.http.get<ActionBatchDetailDto>(`/api/history/${encodeURIComponent(id)}`);
  }

  /** `202` with the queued undo batch; `409` with the reason when the batch cannot be undone now. */
  undo(id: string): Observable<ActionBatchDto> {
    return this.http.post<ActionBatchDto>(`/api/history/${encodeURIComponent(id)}/undo`, null);
  }
}
