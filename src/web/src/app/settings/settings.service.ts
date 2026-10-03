import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { SetupService } from '../setup/setup.service';
import {
  AnalysisSettingsUpdate,
  AttachmentsUpdate,
  ClaudeSettingsUpdate,
  PromptTemplateDto,
  ProtectionUpdate,
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

  getDefaultPrompt(): Observable<PromptTemplateDto> {
    return this.http.get<PromptTemplateDto>('/api/analysis/prompt/default');
  }
}
