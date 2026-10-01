import { TestBed } from '@angular/core/testing';
import { MatSnackBar } from '@angular/material/snack-bar';
import { of } from 'rxjs';
import { AppSettings, GoogleClientSettings, SetupService } from '../setup.service';
import { GoogleClientStep, SAVED_SECRET_MASK } from './google-client-step.component';

const CLIENT_ID = 'synthetic-id.apps.googleusercontent.com';
const REDIRECT = 'http://localhost:4200/api/auth/google/callback';

function settings(googleClient: GoogleClientSettings): AppSettings {
  return {
    ollamaBaseUrl: 'http://localhost:11434',
    chatModel: null,
    embeddingModel: null,
    actionLabelName: 'Example-Action',
    deleteLabelName: 'Example-Delete',
    setupWizardSeen: false,
    googleClient,
  };
}

describe('GoogleClientStep', () => {
  let setup: {
    getSettings: ReturnType<typeof vi.fn>;
    getGoogleStatus: ReturnType<typeof vi.fn>;
    saveGoogleClient: ReturnType<typeof vi.fn>;
  };

  async function render(client: GoogleClientSettings) {
    setup = {
      getSettings: vi.fn(() => of(settings(client))),
      getGoogleStatus: vi.fn(() =>
        of({
          connected: false,
          reason: 'not_connected',
          accountEmail: null,
          scopes: [],
          missingScopes: [],
          redirectUri: REDIRECT,
        }),
      ),
      saveGoogleClient: vi.fn(() =>
        of(settings({ clientId: CLIENT_ID, secretSet: true, lockedByEnv: false })),
      ),
    };
    TestBed.configureTestingModule({
      imports: [GoogleClientStep],
      providers: [
        { provide: SetupService, useValue: setup },
        { provide: MatSnackBar, useValue: { open: vi.fn() } },
      ],
    });
    const fixture = TestBed.createComponent(GoogleClientStep);
    await fixture.whenStable();
    return { fixture, el: fixture.nativeElement as HTMLElement, cmp: fixture.componentInstance };
  }

  const q = <T extends Element>(el: HTMLElement, id: string) =>
    el.querySelector<T>(`[data-testid="${id}"]`);

  it('empty: both fields editable and required, secret is a password', async () => {
    const { el, cmp } = await render({ clientId: null, secretSet: false, lockedByEnv: false });
    expect(cmp.editing()).toBe(true);
    expect(q<HTMLInputElement>(el, 'client-secret')!.type).toBe('password');
    expect(q(el, 'save')).not.toBeNull();
    expect(cmp.form.controls.clientId.hasError('required')).toBe(true);
    expect(cmp.form.controls.clientSecret.hasError('required')).toBe(true);
  });

  it('validates the client ID suffix', async () => {
    const { cmp } = await render({ clientId: null, secretSet: false, lockedByEnv: false });
    cmp.form.controls.clientId.setValue('not-a-google-id');
    expect(cmp.form.controls.clientId.hasError('googleClientId')).toBe(true);
    cmp.form.controls.clientId.setValue(CLIENT_ID);
    expect(cmp.form.controls.clientId.valid).toBe(true);
  });

  it('does not save an invalid form', async () => {
    const { cmp } = await render({ clientId: null, secretSet: false, lockedByEnv: false });
    cmp.save();
    expect(setup.saveGoogleClient).not.toHaveBeenCalled();
    expect(cmp.form.controls.clientId.touched).toBe(true);
  });

  it('saves trimmed values and switches to the saved state', async () => {
    const { fixture, el, cmp } = await render({
      clientId: null,
      secretSet: false,
      lockedByEnv: false,
    });
    cmp.form.setValue({ clientId: ` ${CLIENT_ID} `, clientSecret: ' s3cret ' });
    cmp.save();
    await fixture.whenStable();
    expect(setup.saveGoogleClient).toHaveBeenCalledWith({
      clientId: CLIENT_ID,
      clientSecret: 's3cret',
    });
    expect(cmp.editing()).toBe(false);
    expect(q<HTMLInputElement>(el, 'client-secret')!.value).toBe(SAVED_SECRET_MASK);
  });

  it('secretSet: shows the mask and Replace, never the secret', async () => {
    const { fixture, el, cmp } = await render({
      clientId: CLIENT_ID,
      secretSet: true,
      lockedByEnv: false,
    });
    expect(cmp.editing()).toBe(false);
    const secret = q<HTMLInputElement>(el, 'client-secret')!;
    expect(secret.value).toBe(SAVED_SECRET_MASK);
    expect(secret.readOnly).toBe(true);
    expect(q(el, 'save')).toBeNull();

    q<HTMLButtonElement>(el, 'replace')!.click();
    await fixture.whenStable();
    expect(cmp.editing()).toBe(true);
    expect(q<HTMLInputElement>(el, 'client-secret')!.type).toBe('password');
    expect(cmp.form.controls.clientId.value).toBe(CLIENT_ID);
    expect(cmp.form.controls.clientSecret.hasError('required')).toBe(true);
  });

  it('lockedByEnv: read-only with "Set in .env", no Save or Replace', async () => {
    const { el, cmp } = await render({ clientId: CLIENT_ID, secretSet: true, lockedByEnv: true });
    expect(cmp.editing()).toBe(false);
    expect(q(el, 'locked-note')!.textContent).toContain('Set in .env');
    expect(q<HTMLInputElement>(el, 'client-id')!.readOnly).toBe(true);
    expect(q(el, 'save')).toBeNull();
    expect(q(el, 'replace')).toBeNull();
  });

  it('shows the redirect URI from the auth status', async () => {
    const { el } = await render({ clientId: null, secretSet: false, lockedByEnv: false });
    expect(q<HTMLInputElement>(el, 'redirect-uri')!.value).toBe(REDIRECT);
  });
});
