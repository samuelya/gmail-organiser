import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { LabelsSettingsSection } from './labels-settings.component';
import {
  LabelSettings,
  MAX_DOCUMENT_TYPE_PARENT_LENGTH,
  MAX_LABEL_NAME_LENGTH,
  PROMPT_PLACEHOLDERS,
} from './settings.models';

const labels: LabelSettings = {
  actionLabelName: 'Synthetic/Act',
  deleteLabelName: 'Synthetic-Bin',
  documentTypeParent: null,
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

  it('shows server field errors, including a parent clash when the parent was not edited', async () => {
    await render({ ...labels, documentTypeParent: 'Synthetic/Docs' });
    await type('delete-label-name', 'Synthetic/Old');
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
    expect(q('document-type-parent-server-error')!.textContent).toContain(
      'Must differ from the action and delete labels.',
    );
  });

  it('lets a save through again once another name fixes a parent clash', async () => {
    await render({ ...labels, documentTypeParent: 'Synthetic/Docs' });
    await type('action-label-name', 'Synthetic/Todo');
    await save();
    http
      .expectOne({ method: 'PUT', url: '/api/settings' })
      .flush(
        { errors: { documentTypeParent: ['Must differ from the action and delete labels.'] } },
        { status: 400, statusText: 'Bad Request' },
      );
    await fixture.whenStable();
    expect(section.documentTypeParent.hasError('server')).toBe(true);
    await type('action-label-name', 'Synthetic-Todo');
    await save();
    const req = http.expectOne({ method: 'PUT', url: '/api/settings' });
    expect(req.request.body).toEqual({ actionLabelName: 'Synthetic-Todo' });
    req.flush({
      ...labels,
      actionLabelName: 'Synthetic-Todo',
      documentTypeParent: 'Synthetic/Docs',
    });
  });

  it('shows the document-type parent with its hint, empty when off', async () => {
    await render();
    expect(q<HTMLInputElement>('documentTypeParent')!.value).toBe('');
    expect(el.textContent).toContain('Empty turns document-type labels off');
  });

  it('validates the document-type parent length, levels and path', async () => {
    await render();
    const parent = section.documentTypeParent;
    for (const [value, error] of [
      ['x'.repeat(MAX_DOCUMENT_TYPE_PARENT_LENGTH + 1), 'maxlength'],
      ['A/B/C/D/E', 'maxLevels'],
      ['A//B', 'labelPath'],
      ['Trail/', 'labelPath'],
    ] as const) {
      parent.setValue(value);
      expect(parent.hasError(error)).toBe(true);
    }
    parent.setValue('A/B/C/D');
    expect(parent.valid).toBe(true);
    parent.setValue('');
    expect(parent.valid).toBe(true);
  });

  it('rejects a parent equal to, above or under the action or delete label', async () => {
    await render();
    for (const value of ['synthetic/act', 'Synthetic', 'Synthetic/Act/Bills', 'SYNTHETIC-BIN']) {
      await type('documentTypeParent', value);
      expect(section.form.hasError('parentClash')).toBe(true);
    }
    expect(q('parent-clash-error')).not.toBeNull();
    await save();
    http.expectNone('/api/settings');
    await type('documentTypeParent', 'Synthetic-Docs');
    expect(section.form.hasError('parentClash')).toBe(false);
  });

  it('sends a new parent trimmed, and "" only when clearing a saved one', async () => {
    await render();
    await type('documentTypeParent', '');
    expect(section.changes()).toEqual({});
    await type('documentTypeParent', '  Synthetic/Docs ');
    await save();
    const req = http.expectOne({ method: 'PUT', url: '/api/settings' });
    expect(req.request.body).toEqual({ documentTypeParent: 'Synthetic/Docs' });
    req.flush({ ...labels, documentTypeParent: 'Synthetic/Docs' });
    await fixture.whenStable();
    expect(section.changes()).toEqual({});
    await type('documentTypeParent', '  ');
    await save();
    const clear = http.expectOne({ method: 'PUT', url: '/api/settings' });
    expect(clear.request.body).toEqual({ documentTypeParent: '' });
    clear.flush({ ...labels, documentTypeParent: null });
    await fixture.whenStable();
    expect(section.changes()).toEqual({});
  });

  it('lists the {{documentTypes}} prompt placeholder', () => {
    expect(PROMPT_PLACEHOLDERS.find((p) => p.name === '{{documentTypes}}')?.help).toContain(
      'never asks for a document-type label',
    );
  });
});
