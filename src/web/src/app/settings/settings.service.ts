import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { SetupService } from '../setup/setup.service';
import {
  AnalysisSettingsUpdate,
  AppsScriptConfigDto,
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

  /** Replaces the whole Apps Script block. */
  saveAppsScript(changes: AppsScriptUpdate): Observable<SettingsDto> {
    return this.setup.saveSettings(changes) as Observable<SettingsDto>;
  }

  /** The script's `CONFIG` block generated from the saved settings. */
  appsScriptConfig(): Observable<AppsScriptConfigDto> {
    return this.http.get<AppsScriptConfigDto>('/api/rules/apps-script/config');
  }

  /** A partial update of the action and delete label names: names left out stay unchanged. */
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
