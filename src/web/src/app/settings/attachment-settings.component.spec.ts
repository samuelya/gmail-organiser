import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { LlmModels, OllamaModel } from '../core/llm.service';
import { AttachmentSettingsSection, visionChoices } from './attachment-settings.component';
import { AttachmentSettings, AttachmentsUpdate } from './settings.models';

const MB = 1024 * 1024;

const attachments = (over: Partial<AttachmentSettings> = {}): AttachmentSettings => ({
  enabled: true,
  types: [
    { type: 'pdf', enabled: true },
    { type: 'image', enabled: true },
    { type: 'spreadsheet', enabled: true },
    { type: 'csv', enabled: true },
    { type: 'word_document', enabled: true },
    { type: 'presentation', enabled: true },
    { type: 'plain_text', enabled: true },
    { type: 'archive', enabled: false },
    { type: 'other', enabled: false },
  ],
  maxBytes: 10 * MB,
  maxImageBytes: 10 * MB,
  maxChars: 4000,
  maxPerMessage: 5,
  ...over,
});

const model = (name: string, capabilities: string[]): OllamaModel => ({
  name,
  sizeBytes: 1,
  family: 'test',
  parameterSize: '1B',
  capabilities,
});

const models = (chatModels: OllamaModel[]): LlmModels => ({
  reachable: true,
  version: '0.0.1-test',
  chatModels,
  embeddingModels: [],
  error: null,
});

describe('AttachmentSettingsSection', () => {
  let fixture: ComponentFixture<AttachmentSettingsSection>;
  let section: AttachmentSettingsSection;
  let emitted: AttachmentsUpdate[];
  let el: HTMLElement;
  let http: HttpTestingController;
  const q = <T extends HTMLElement = HTMLElement>(id: string) =>
    el.querySelector<T>(`[data-testid="${id}"]`);
  const toggle = (id: string) => q(id)!.querySelector('button')!;

  async function render(
    settings: AttachmentSettings | null = attachments(),
    visionModel?: string | null,
  ) {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(AttachmentSettingsSection);
    section = fixture.componentInstance;
    emitted = [];
    section.changed.subscribe((c) => emitted.push(c));
    fixture.componentRef.setInput('settings', settings);
    fixture.componentRef.setInput('visionModel', visionModel);
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

  const fieldError = (id: string) =>
    q(id)!.closest('mat-form-field')!.querySelector('mat-error')?.textContent?.trim();

  afterEach(() => http.verify());

  it('binds the toggles and limits to the settings, sizes in MB', async () => {
    await render(attachments({ maxImageBytes: 5 * MB }));
    expect(toggle('attachments-enabled').getAttribute('aria-checked')).toBe('true');
    expect(toggle('type-pdf').getAttribute('aria-checked')).toBe('true');
    expect(toggle('type-other').getAttribute('aria-checked')).toBe('false');
    expect(q<HTMLInputElement>('maxBytes')!.value).toBe('10');
    expect(q<HTMLInputElement>('maxImageBytes')!.value).toBe('5');
    expect(q<HTMLInputElement>('maxChars')!.value).toBe('4000');
    expect(q<HTMLInputElement>('maxPerMessage')!.value).toBe('5');

    toggle('attachments-enabled').click();
    await fixture.whenStable();
    expect(section.c.enabled.value).toBe(false);
  });

  it('steps the MB inputs in whole MB and keeps the 64 KB minimum', async () => {
    await render(attachments());
    const input = q<HTMLInputElement>('maxBytes')!;
    expect(input.getAttribute('min')).toBe('0');
    input.stepUp();
    expect(input.value).toBe('11');

    await type('maxBytes', '0.03');
    expect(section.c.maxBytes.hasError('min')).toBe(true);
    await type('maxBytes', '0.0625');
    expect(section.c.maxBytes.valid).toBe(true);
  });

  it('keeps the form disabled until settings arrive', async () => {
    await render(null);
    expect(section.form.disabled).toBe(true);
    expect(q<HTMLButtonElement>('save-attachments')!.disabled).toBe(true);
  });

  it('shows archives as always ignored', async () => {
    await render(attachments({ types: [{ type: 'archive', enabled: true }] }));
    expect(toggle('type-archive').disabled).toBe(true);
    expect(toggle('type-archive').getAttribute('aria-checked')).toBe('false');
    expect(section.c.types.controls.archive.disabled).toBe(true);
    expect(section.changes()).toEqual({});
  });

  it('shows the API ranges as validation messages and blocks the save', async () => {
    await render();
    await type('maxBytes', '30');
    expect(fieldError('maxBytes')).toBe('Enter a size from 64 KB (0.0625 MB) to 25 MB.');
    await type('maxChars', '100');
    expect(fieldError('maxChars')).toBe('Enter a number from 500 to 50000.');
    await type('maxPerMessage', '2.5');
    expect(fieldError('maxPerMessage')).toBe('Enter a whole number.');
    await type('maxPerMessage', '');
    expect(fieldError('maxPerMessage')).toBe('Max attachments per message is required.');

    section.save();
    expect(emitted).toEqual([]);
  });

  it('emits only the changed fields, sizes in bytes, types as listed entries', async () => {
    await render();
    toggle('attachments-enabled').click();
    toggle('type-other').click();
    await type('maxBytes', '2.5');
    await type('maxPerMessage', '8');
    q('save-attachments')!.click();
    await fixture.whenStable();

    expect(emitted).toEqual([
      {
        attachments: {
          enabled: false,
          types: [{ type: 'other', enabled: true }],
          maxBytes: 2.5 * MB,
          maxPerMessage: 8,
        },
      },
    ]);
  });

  it('Discard puts the form back to the loaded settings', async () => {
    await render();
    await type('maxChars', '9000');
    q('discard-attachments')!.click();
    await fixture.whenStable();
    expect(q<HTMLInputElement>('maxChars')!.value).toBe('4000');
    expect(section.changes()).toEqual({});
  });

  it('shows server field errors inline and type errors below the list', async () => {
    await render();
    fixture.componentRef.setInput('serverErrors', {
      'attachments.maxChars': ['Synthetic range error.'],
      'attachments.types[0].enabled': ['Synthetic type error.'],
    });
    await fixture.whenStable();
    expect(fieldError('maxChars')).toBe('Synthetic range error.');
    expect(q('attachments-error')!.textContent!.trim()).toBe('Synthetic type error.');
  });

  it('hides the image-reading choice when the API has no imageMode', async () => {
    await render(attachments(), null);
    expect(q('imageMode')).toBeNull();
    expect(section.changes()).toEqual({});
  });

  it('shows the vision model picker only for the vision mode and sends the model', async () => {
    await render(attachments({ imageMode: 'ocr' }), null);
    expect(q('imageMode')).not.toBeNull();
    expect(q('vision-model')).toBeNull();
    http.expectNone('/api/llm/models');

    q('image-mode-vision')!.querySelector('input')!.click();
    await fixture.whenStable();
    http
      .expectOne('/api/llm/models')
      .flush(
        models([
          model('test-vision:1b', ['completion', 'vision']),
          model('test-chat:1b', ['completion']),
        ]),
      );
    await fixture.whenStable();
    expect(q('vision-model')).not.toBeNull();
    expect(section.vision().options.map((o) => o.value)).toEqual(['test-vision:1b']);

    section.c.visionModel.setValue('test-vision:1b');
    section.save();
    expect(emitted).toEqual([
      { attachments: { imageMode: 'vision' }, visionModel: 'test-vision:1b' },
    ]);
  });
});

describe('visionChoices', () => {
  it('filters to vision-capable models when Ollama reports capabilities', () => {
    const choice = visionChoices(
      [model('test-vision:1b', ['completion', 'vision']), model('test-chat:1b', ['completion'])],
      null,
    );
    expect(choice.filtered).toBe(true);
    expect(choice.options.map((o) => o.value)).toEqual(['test-vision:1b']);
  });

  it('lists every model when no capabilities are reported, plus a missing saved one', () => {
    const choice = visionChoices([model('test-a:1b', []), model('test-b:1b', [])], 'test-gone:1b');
    expect(choice.filtered).toBe(false);
    expect(choice.options.map((o) => o.value)).toEqual(['test-gone:1b', 'test-a:1b', 'test-b:1b']);
  });

  it('keeps models with unknown capabilities, labelled, like the API', () => {
    const choice = visionChoices(
      [
        model('test-vision:1b', ['vision']),
        model('test-chat:1b', ['completion']),
        model('test-unknown:1b', []),
      ],
      'test-unknown:1b',
    );
    expect(choice.filtered).toBe(true);
    expect(choice.unknown).toBe(true);
    expect(choice.options.map((o) => o.value)).toEqual(['test-vision:1b', 'test-unknown:1b']);
    expect(choice.options[1].label).toContain('(capabilities unknown)');
  });

  it('labels a saved model on the server without vision apart from a missing one', () => {
    const models = [model('test-vision:1b', ['vision']), model('test-chat:1b', ['completion'])];
    const onServer = visionChoices(models, 'test-chat:1b').options[0];
    expect(onServer.value).toBe('test-chat:1b');
    expect(onServer.label).toContain('(no vision capability reported)');
    expect(onServer.label).not.toContain('not found');
    expect(visionChoices(models, 'test-gone:1b').options[0].label).toContain(
      '(not found on the server)',
    );
    expect(visionChoices(models, null).unknown).toBe(false);
  });
});
