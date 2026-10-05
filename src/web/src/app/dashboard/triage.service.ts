import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { TriageMetricsDto } from './triage.models';

/** Triage metrics API: the newest snapshot, 90 days of daily history and the LLM hours. */
@Injectable({ providedIn: 'root' })
export class TriageService {
  private readonly http = inject(HttpClient);

  get(): Observable<TriageMetricsDto> {
    return this.http.get<TriageMetricsDto>('/api/dashboard/triage');
  }

  /** Takes a snapshot now and answers with the metrics including it. */
  snapshot(): Observable<TriageMetricsDto> {
    return this.http.post<TriageMetricsDto>('/api/dashboard/triage/snapshot', null);
  }
}
