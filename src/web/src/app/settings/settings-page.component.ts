import { ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { MatCardModule } from '@angular/material/card';
import { PageHeader } from '../layout/page-header';
import { GoogleClientSettings } from '../setup/setup.service';
import { ConnectGmailStep } from '../setup/steps/connect-gmail-step.component';
import { GoogleClientStep } from '../setup/steps/google-client-step.component';
import { ModelsStep } from '../setup/steps/models-step.component';
import { OllamaUrlStep } from '../setup/steps/ollama-url-step.component';

/**
 * `/settings` (M1): the wizard's step components, one section each. Every step saves only its own
 * fields (the API leaves missing fields unchanged), so saving one section never resets another.
 */
@Component({
  selector: 'app-settings-page',
  imports: [
    MatCardModule,
    PageHeader,
    GoogleClientStep,
    ConnectGmailStep,
    OllamaUrlStep,
    ModelsStep,
  ],
  templateUrl: './settings-page.component.html',
  styles: `
    .section-title {
      font: var(--mat-sys-title-medium);
      margin: 0;
    }
    .section-hint {
      font: var(--mat-sys-body-medium);
      color: var(--mat-sys-on-surface-variant);
      margin: 0.25rem 0 1rem;
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SettingsPage {
  /** The Google section reports a saved client ID and secret; the Gmail section enables Connect from it. */
  readonly clientSaved = signal(false);
  /** `null` until the Ollama URL is saved here: the models section lists against the saved URL. */
  readonly ollamaUrl = signal<string | null>(null);

  onClientChange(client: GoogleClientSettings): void {
    this.clientSaved.set(!!client.clientId && client.secretSet);
  }
}
