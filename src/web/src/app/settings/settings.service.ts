import { HttpClient, HttpContext } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { AsyncSubject, concatMap, defer, finalize, map, Observable, of, take } from 'rxjs';
import { QUIET_STATUSES } from '../core/error.interceptor';
import { JobDto } from '../core/jobs.models';
import { SetupService } from '../setup/setup.service';
import {
  PackSettings,
  RetentionStatusDto,
  RetentionUpdate,
  TaxonomyUpdate,
  TriageModelSettings,
  TriageUpdate,
} from './triage-settings.models';
import {
  AnalysisSettingsUpdate,
  AppsScriptConfigDto,
  AppsScriptSettings,
  AppsScriptUpdate,
  AttachmentsUpdate,
  ClaudeSettingsUpdate,
  LabelSettingsUpdate,
  PromptTemplateDto,
  ProtectionUpdate,
  PurgeResponse,
  SettingsDto,
} from './settings.models';

/**
 * Settings page API. Reads and saves go through {@link SetupService} so every section shares one
 * cached `GET /api/settings`; `AppSettings` there types only the setup fields.
 */
@Injectable({ providedIn: 'root' })
export class SettingsService {
  private readonly http = inject(HttpClient);
  private readonly setup = inject(SetupService);
  /** Completes when the last queued Apps Script save settles. */
  private appsScriptTurn: Observable<void> = of(undefined);

  getSettings(): Observable<SettingsDto> {
    return this.setup.getSettings() as Observable<SettingsDto>;
  }

  /** A partial update: fields left out stay unchanged. */
  saveAnalysis(changes: AnalysisSettingsUpdate): Observable<SettingsDto> {
    return this.setup.saveSettings(changes) as Observable<SettingsDto>;
  }

  /** A partial update of the attachment block (and the vision model): fields left out stay unchanged. */
  saveAttachments(changes: AttachmentsUpdate): Observable<SettingsDto> {
    return this.setup.saveSettings(changes) as Observable<SettingsDto>;
  }

  /** A partial update of the Claude review fields: fields left out stay unchanged. */
  saveClaude(changes: ClaudeSettingsUpdate): Observable<SettingsDto> {
    return this.setup.saveSettings(changes) as Observable<SettingsDto>;
  }

  /** A partial update of the protection rules: rules left out stay unchanged. */
  saveProtection(changes: ProtectionUpdate): Observable<SettingsDto> {
    return this.setup.saveSettings(changes) as Observable<SettingsDto>;
  }

  /**
   * Replaces the whole Apps Script block. Saves run one at a time in subscribe order, and a `build`
   * function gets the block as the previous save left it, so two sections never write back each
   * other's stale fields.
   */
  saveAppsScript(
    changes: AppsScriptUpdate | ((saved: AppsScriptSettings) => AppsScriptUpdate),
  ): Observable<SettingsDto> {
    return defer(() => {
      const previous = this.appsScriptTurn;
      const turn = new AsyncSubject<void>();
      this.appsScriptTurn = turn;
      return previous.pipe(
        concatMap(() =>
          typeof changes === 'function'
            ? this.getSettings().pipe(
                take(1),
                map((s) => changes(s.appsScript!)),
              )
            : of(changes),
        ),
        concatMap((request) => this.setup.saveSettings(request) as Observable<SettingsDto>),
        finalize(() => {
          turn.next();
          turn.complete();
        }),
      );
    });
  }

  /** A partial update of the retention block: mail types left out stay unchanged. */
  saveRetention(changes: RetentionUpdate): Observable<SettingsDto> {
    return this.setup.saveSettings(changes) as Observable<SettingsDto>;
  }

  /** A partial update of the taxonomy fields; the blocked labels are replaced whole. */
  saveTaxonomy(changes: TaxonomyUpdate): Observable<SettingsDto> {
    return this.setup.saveSettings(changes) as Observable<SettingsDto>;
  }

  /** A partial update of the triage model and pack fields: fields left out stay unchanged. */
  saveTriage(
    changes: TriageUpdate,
  ): Observable<SettingsDto & Partial<TriageModelSettings & PackSettings>> {
    return this.setup.saveSettings(changes) as Observable<SettingsDto>;
  }

  /** The last and next retention sweep and at most how many messages one would mark now. */
  retentionStatus(): Observable<RetentionStatusDto> {
    return this.http.get<RetentionStatusDto>('/api/clean-up/retention');
  }

  /** Queues a retention sweep; a 409 (retention off, or a sweep active) is left to the caller. */
  runRetention(): Observable<JobDto> {
    return this.http.post<JobDto>('/api/clean-up/retention/run', null, {
      context: new HttpContext().set(QUIET_STATUSES, [409]),
    });
  }

  /** The script's `CONFIG` block generated from the saved settings. */
  appsScriptConfig(): Observable<AppsScriptConfigDto> {
    return this.http.get<AppsScriptConfigDto>('/api/rules/apps-script/config');
  }

  /** A partial update of the label names: names left out stay unchanged; `""` clears the parent. */
  updateLabels(changes: LabelSettingsUpdate): Observable<SettingsDto> {
    return this.setup.saveSettings(changes) as Observable<SettingsDto>;
  }

  /** Wipes the local data the app fetched or produced; `confirm` must be the confirmation word. */
  purge(confirm: string): Observable<PurgeResponse> {
    return this.http.post<PurgeResponse>('/api/settings/purge', { confirm });
  }

  getDefaultPrompt(): Observable<PromptTemplateDto> {
    return this.http.get<PromptTemplateDto>('/api/analysis/prompt/default');
  }
}
