import { DOCUMENT } from '@angular/common';
import { HttpClient } from '@angular/common/http';
import { inject, Injectable, InjectionToken } from '@angular/core';
import { Observable } from 'rxjs';

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
  googleClient: GoogleClientSettings;
}

/** `GoogleClientRequest` for `PUT /api/settings/google-client`; the API needs both values. */
export interface GoogleClientRequest {
  clientId: string;
  clientSecret: string;
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

  getSettings(): Observable<AppSettings> {
    return this.http.get<AppSettings>('/api/settings');
  }

  saveGoogleClient(request: GoogleClientRequest): Observable<AppSettings> {
    return this.http.put<AppSettings>('/api/settings/google-client', request);
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

  /** Leaves the app for Google's consent screen; the API redirects back to `/setup?gmail=…`. */
  connectGoogle(): void {
    this.navigateTo(GOOGLE_CONNECT_URL);
  }
}
