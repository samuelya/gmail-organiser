import { HttpClient, HttpParams } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import {
  AnalysisRunDto,
  AnalysisSelection,
  AnalysisSummaryDto,
  CompareRunRequest,
  GroupingPreviewDto,
} from './analysis.models';

/** Analysis preview, runs and summary. Run progress comes live from `JobsService` by `jobId`. */
@Injectable({ providedIn: 'root' })
export class AnalysisService {
  private readonly http = inject(HttpClient);

  /** Grouping and estimated LLM calls for a selection, without any model call. */
  preview(selection: AnalysisSelection): Observable<GroupingPreviewDto> {
    return this.http.post<GroupingPreviewDto>('/api/analysis/preview', selection);
  }

  /** `202` with the queued run; `409` when no chat model is selected. */
  start(selection: AnalysisSelection): Observable<AnalysisRunDto> {
    return this.http.post<AnalysisRunDto>('/api/analysis/runs', selection);
  }

  /**
   * `202` with the queued re-analysis: its results are stored next to the current suggestions until the
   * user picks; `400` for an empty or too large selection, `409` when no chat model is selected.
   */
  startCompareRun(request: CompareRunRequest): Observable<AnalysisRunDto> {
    return this.http.post<AnalysisRunDto>('/api/analysis/compare-runs', request);
  }

  /** Newest first: active (queued or running) or finished runs. */
  listRuns(active: boolean, limit: number): Observable<AnalysisRunDto[]> {
    const params = new HttpParams().set('active', active).set('limit', limit);
    return this.http.get<AnalysisRunDto[]>('/api/analysis/runs', { params });
  }

  /** A running run stops after its current group; `409` when it has already finished. */
  cancel(id: string): Observable<AnalysisRunDto> {
    return this.http.post<AnalysisRunDto>(
      `/api/analysis/runs/${encodeURIComponent(id)}/cancel`,
      null,
    );
  }

  summary(): Observable<AnalysisSummaryDto> {
    return this.http.get<AnalysisSummaryDto>('/api/analysis/summary');
  }
}
