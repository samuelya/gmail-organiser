import { DOCUMENT } from '@angular/common';
import { HttpClient } from '@angular/common/http';
import { inject, Injectable, InjectionToken } from '@angular/core';
import { Observable, of, shareReplay, tap } from 'rxjs';
import type {
  AnalysisSettingsUpdate,
  AppsScriptUpdate,
  AttachmentsUpdate,
  ClaudeSettingsUpdate,
  LabelSettingsUpdate,
  ProtectionUpdate,
} from '../settings/settings.models';
import type {
  RetentionUpdate,
  TaxonomyUpdate,
  TriageUpdate,
} from '../settings/triage-settings.models';

/** `GoogleClientDto`: the secret itself is never returned. */
export interface GoogleClientSettings {
  clientId: string | null;
  secretSet: boolean;
  lockedByEnv: boolean;
}

/** `SettingsDto` from `GET /api/settings`. */
export interface AppSettings {
  ollamaBaseUrl: string;
  chatModel: string | null;
  embeddingModel: string | null;
  actionLabelName: string;
  deleteLabelName: string;
  setupWizardSeen: boolean;
  /** Messages per Gmail page the fetch reads (100–5000). */
  fetchChunkSize: number;
  googleClient: GoogleClientSettings;
}

/** `GoogleClientRequest` for `PUT /api/settings/google-client`; the API needs both values. */
export interface GoogleClientRequest {
  clientId: string;
  clientSecret: string;
}

/**
 * `UpdateSettingsRequest` for `PUT /api/settings`: a partial update where an omitted (or `null`)
 * value stays unchanged and an empty model name clears the model.
 */
export interface UpdateSettingsRequest
  extends
    AnalysisSettingsUpdate,
    AttachmentsUpdate,
    ClaudeSettingsUpdate,
    ProtectionUpdate,
    Partial<AppsScriptUpdate>,
    Partial<RetentionUpdate>,
    TaxonomyUpdate,
    TriageUpdate,
    LabelSettingsUpdate {
  ollamaBaseUrl?: string;
  chatModel?: string;
  embeddingModel?: string;
  setupWizardSeen?: boolean;
  fetchChunkSize?: number;
}

/** `GmailConnectionStatusDto` from `GET /api/auth/google/status`. */
export interface GoogleAuthStatus {
  connected: boolean;
  /** `not_connected` or `reauth_required` when not connected. */
  reason: string | null;
  accountEmail: string | null;
  scopes: string[];
  missingScopes: string[];
  /** The callback URI to register in the Google OAuth client. */
  redirectUri: string;
}

/** `SetupStatusDto` from `GET /api/setup/status`. */
export interface SetupStatus {
  /** True when a client ID and secret are saved, or always with the fake Gmail. */
  googleClientConfigured: boolean;
  gmailConnected: boolean;
  gmailReauthRequired: boolean;
  ollamaReachable: boolean;
  chatModelSelected: boolean;
  embeddingModelSelected: boolean;
  wizardSeen: boolean;
  complete: boolean;
}

export const GOOGLE_CONNECT_URL = '/api/auth/google/start';

/** The page the OAuth callback returns to (`returnTo` on the start endpoint). */
export type ConnectReturnTo = 'setup' | 'settings';

/** Full-page navigation (the OAuth start endpoint redirects to Google, so it can't be an XHR call). */
export const NAVIGATE_TO = new InjectionToken<(url: string) => void>('NAVIGATE_TO', {
  providedIn: 'root',
  factory: () => {
    const location = inject(DOCUMENT).location;
    return (url: string) => location.assign(url);
  },
});

/** Setup wizard API: settings for the Google OAuth client and the Gmail connection. */
@Injectable({ providedIn: 'root' })
export class SetupService {
  private readonly http = inject(HttpClient);
  private readonly navigateTo = inject(NAVIGATE_TO);
  /** One `GET /api/settings` shared by every section; replaced by each save's response, dropped on error. */
  private settings$: Observable<AppSettings> | null = null;

  getSettings(): Observable<AppSettings> {
    this.settings$ ??= this.http
      .get<AppSettings>('/api/settings')
      .pipe(tap({ error: () => (this.settings$ = null) }), shareReplay(1));
    return this.settings$;
  }

  saveSettings(request: UpdateSettingsRequest): Observable<AppSettings> {
    return this.http.put<AppSettings>('/api/settings', request).pipe(tap((s) => this.cache(s)));
  }

  saveGoogleClient(request: GoogleClientRequest): Observable<AppSettings> {
    return this.http
      .put<AppSettings>('/api/settings/google-client', request)
      .pipe(tap((s) => this.cache(s)));
  }

  getSetupStatus(): Observable<SetupStatus> {
    return this.http.get<SetupStatus>('/api/setup/status');
  }

  getGoogleStatus(): Observable<GoogleAuthStatus> {
    return this.http.get<GoogleAuthStatus>('/api/auth/google/status');
  }

  disconnectGoogle(): Observable<void> {
    return this.http.post<void>('/api/auth/google/disconnect', null);
  }

  private cache(settings: AppSettings): void {
    this.settings$ = of(settings);
  }

  /**
   * Leaves the app for Google's consent screen; the API redirects back to `/<returnTo>?gmail=…`.
   * The wizard sends no param (the API defaults to setup).
   */
  connectGoogle(returnTo: ConnectReturnTo = 'setup'): void {
    this.navigateTo(
      returnTo === 'setup' ? GOOGLE_CONNECT_URL : `${GOOGLE_CONNECT_URL}?returnTo=${returnTo}`,
    );
  }
}
