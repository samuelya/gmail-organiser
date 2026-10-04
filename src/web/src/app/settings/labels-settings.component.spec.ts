import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { LabelsSettingsSection } from './labels-settings.component';
import { LabelSettings, MAX_LABEL_NAME_LENGTH } from './settings.models';

const labels: LabelSettings = {
  actionLabelName: 'Synthetic/Act',
  deleteLabelName: 'Synthetic-Bin',
};

describe('LabelsSettingsSection', () => {
  let fixture: ComponentFixture<LabelsSettingsSection>;
  let section: LabelsSettingsSection;
  let http: HttpTestingController;
  let el: HTMLElement;
  const q = <T extends HTMLElement = HTMLElement>(id: string) =>
    el.querySelector<T>(`[data-testid="${id}"]`);

  async function render(settings: LabelSettings | null = labels) {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(LabelsSettingsSection);
    fixture.componentRef.setInput('settings', settings);
    section = fixture.componentInstance;
    el = fixture.nativeElement as HTMLElement;
    await fixture.whenStable();
  }

  async function type(id: string, value: string) {
    const input = q<HTMLInputElement>(id)!;
    input.value = value;
    input.dispatchEvent(new Event('input'));
    input.dispatchEvent(new Event('blur'));
    await fixture.whenStable();
  }

  async function save() {
    q<HTMLButtonElement>('save-labels')!.click();
    await fixture.whenStable();
  }

  afterEach(() => http.verify());

  it('is disabled until the names load, then shows them', async () => {
    await render(null);
    expect(section.form.disabled).toBe(true);
    expect(q<HTMLButtonElement>('save-labels')!.disabled).toBe(true);
    fixture.componentRef.setInput('settings', labels);
    await fixture.whenStable();
    expect(q<HTMLInputElement>('action-label-name')!.value).toBe('Synthetic/Act');
    expect(q<HTMLInputElement>('delete-label-name')!.value).toBe('Synthetic-Bin');
    expect(q('labels-rename-hint')!.textContent).toContain('does not touch Gmail');
  });

  it('validates required, length and label paths', async () => {
    await render();
    const action = section.actionLabelName;
    for (const [value, error] of [
      ['', 'required'],
      ['x'.repeat(MAX_LABEL_NAME_LENGTH + 1), 'maxlength'],
      ['/Lead', 'labelPath'],
      ['Trail/', 'labelPath'],
      ['A//B', 'labelPath'],
      ['A/ /B', 'labelPath'],
    ] as const) {
      action.setValue(value);
      expect(action.hasError(error)).toBe(true);
    }
    action.setValue('A/B/C');
    expect(action.valid).toBe(true);
  });

  it('marks the delete label when both names match ignoring case', async () => {
    await render();
    await type('delete-label-name', 'synthetic/ACT');
    expect(section.form.hasError('labelsMatch')).toBe(true);
    expect(q('labels-match-error')!.textContent).toContain('Must differ from the action label');
    await save();
    http.expectNone('/api/settings');
  });

  it('saves only the changed, trimmed names', async () => {
    await render();
    await type('action-label-name', '  Synthetic/Todo ');
    await save();
    const req = http.expectOne({ method: 'PUT', url: '/api/settings' });
    expect(req.request.body).toEqual({ actionLabelName: 'Synthetic/Todo' });
    req.flush({ ...labels, actionLabelName: 'Synthetic/Todo' });
    await fixture.whenStable();
    expect(section.changes()).toEqual({});
  });

  it('sends nothing when no name changed', async () => {
    await render();
    await save();
    http.expectNone('/api/settings');
  });

  it('shows server field errors and a document-type parent clash', async () => {
    await render();
    await type('delete-label-name', 'Synthetic/Act/Old');
    await save();
    http.expectOne({ method: 'PUT', url: '/api/settings' }).flush(
      {
        errors: {
          deleteLabelName: ['Must not be a Gmail system label.'],
          documentTypeParent: ['Must differ from the action and delete labels.'],
        },
      },
      { status: 400, statusText: 'Bad Request' },
    );
    await fixture.whenStable();
    expect(section.deleteLabelName.getError('server')).toBe('Must not be a Gmail system label.');
    expect(el.textContent).toContain('Must not be a Gmail system label.');
    expect(q('labels-form-error')!.textContent).toContain(
      'Must differ from the action and delete labels.',
    );
  });
});
