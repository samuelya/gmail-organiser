import { BreakpointObserver } from '@angular/cdk/layout';
import {
  afterNextRender,
  ChangeDetectionStrategy,
  Component,
  inject,
  Injector,
  signal,
  viewChild,
} from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatStepper, MatStepperModule } from '@angular/material/stepper';
import { ActivatedRoute, Router } from '@angular/router';
import { map } from 'rxjs';
import { PageHeader } from '../layout/page-header';
import { GoogleAuthStatus, GoogleClientSettings } from './setup.service';
import {
  ConnectGmailStep,
  ConnectResult,
  parseConnectResult,
} from './steps/connect-gmail-step.component';
import { GoogleClientStep } from './steps/google-client-step.component';

export const STEP_GOOGLE_CLIENT = 0;
export const STEP_CONNECT_GMAIL = 1;
export const STEP_OLLAMA = 2;

const WIDE_QUERY = '(min-width: 960px)';

/** `/setup`: a linear wizard where every step can be skipped. Steps 3–5 arrive with the Ollama setup. */
@Component({
  selector: 'app-setup-page',
  imports: [
    MatButtonModule,
    MatCardModule,
    MatStepperModule,
    PageHeader,
    GoogleClientStep,
    ConnectGmailStep,
  ],
  templateUrl: './setup-page.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SetupPage {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly injector = inject(Injector);
  private readonly stepper = viewChild.required(MatStepper);

  readonly wide = toSignal(
    inject(BreakpointObserver)
      .observe(WIDE_QUERY)
      .pipe(map((state) => state.matches)),
    { initialValue: true },
  );

  /** The OAuth callback result, read once; the query params are then cleared. */
  readonly connectResult: ConnectResult | null;
  readonly initialIndex: number;

  /** Per step: done or skipped. Steps 1 and 2 also complete from the loaded state. */
  readonly completed = [false, false, false, false, false].map((done) => signal(done));
  readonly accountEmail = signal<string | null>(null);

  constructor() {
    const params = this.route.snapshot.queryParamMap;
    this.connectResult = parseConnectResult(params.get('gmail'), params.get('reason'));
    this.initialIndex =
      this.connectResult?.kind === 'connected'
        ? STEP_OLLAMA
        : this.connectResult?.kind === 'error'
          ? STEP_CONNECT_GMAIL
          : STEP_GOOGLE_CLIENT;
    if (this.connectResult?.kind === 'connected') {
      // Connecting needed the client, so both earlier steps are complete.
      this.completed[STEP_GOOGLE_CLIENT].set(true);
      this.completed[STEP_CONNECT_GMAIL].set(true);
    }
    if (this.connectResult) {
      void this.router.navigate([], {
        relativeTo: this.route,
        queryParams: {},
        replaceUrl: true,
      });
    }
  }

  onClientChange(client: GoogleClientSettings): void {
    if (client.clientId && client.secretSet) this.completed[STEP_GOOGLE_CLIENT].set(true);
  }

  onStatusChange(status: GoogleAuthStatus): void {
    this.accountEmail.set(status.connected ? status.accountEmail : null);
    if (status.connected) this.completed[STEP_CONNECT_GMAIL].set(true);
  }

  /** "Skip for now" (and "Next" on placeholder steps): completes the step, then moves on once rendered. */
  skip(index: number): void {
    this.completed[index].set(true);
    afterNextRender(() => this.stepper().next(), { injector: this.injector });
  }
}
