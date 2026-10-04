import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { AnalysisSettingsSection } from './analysis-settings.component';
import { ANALYSIS_LIMITS, AnalysisSettings, AnalysisSettingsUpdate } from './settings.models';

const analysis = (over: Partial<AnalysisSettings> = {}): AnalysisSettings => ({
  analysisDefaultCount: 20,
  analysisBodyMaxChars: 4000,
  analysisGroupingMode: 'auto',
  analysisRepresentativesPerGroup: 3,
  analysisMinGroupSize: 3,
  analysisDerivedConfidencePenalty: 0.1,
  analysisClusterDistance: 0.15,
  analysisMemoryShortCircuit: true,
  analysisMemoryMinApprovals: 3,
  bulkApproveThreshold: 0.8,
  autoArchiveOnActionDone: false,
  analysisPromptTemplate: null,
  ...over,
});

describe('AnalysisSettingsSection', () => {
  let fixture: ComponentFixture<AnalysisSettingsSection>;
  let section: AnalysisSettingsSection;
  let emitted: AnalysisSettingsUpdate[];
  let el: HTMLElement;
  const q = <T extends HTMLElement = HTMLElement>(id: string) =>
    el.querySelector<T>(`[data-testid="${id}"]`);

  async function render(settings: AnalysisSettings | null = analysis(), embedding = 'test-embed') {
    TestBed.configureTestingModule({
      providers: [{ provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } }],
    });
    fixture = TestBed.createComponent(AnalysisSettingsSection);
    section = fixture.componentInstance;
    emitted = [];
    section.changed.subscribe((c) => emitted.push(c));
    fixture.componentRef.setInput('settings', settings);
    fixture.componentRef.setInput('embeddingModel', embedding);
    el = fixture.nativeElement as HTMLElement;
    await fixture.whenStable();
  }

  async function type(id: string, value: string) {
    const input = q<HTMLInputElement | HTMLTextAreaElement>(id)!;
    input.value = value;
    input.dispatchEvent(new Event('input'));
    input.dispatchEvent(new Event('blur'));
    await fixture.whenStable();
  }

  const fieldError = (id: string) =>
    q(id)!.closest('mat-form-field')!.querySelector('mat-error')?.textContent?.trim();

  it('renders the values from the settings DTO', async () => {
    await render(
      analysis({ analysisGroupingMode: 'sender_subject', analysisPromptTemplate: 'P {{emails}}' }),
    );
    expect(q<HTMLInputElement>('analysisDefaultCount')!.value).toBe('20');
    expect(q<HTMLInputElement>('analysisBodyMaxChars')!.value).toBe('4000');
    expect(q<HTMLInputElement>('analysisClusterDistance')!.value).toBe('0.15');
    expect(q<HTMLInputElement>('bulkApproveThreshold')!.value).toBe('0.8');
    expect(q('grouping-sender_subject')!.querySelector('input')!.checked).toBe(true);
    expect(q<HTMLTextAreaElement>('analysisPromptTemplate')!.value).toBe('P {{emails}}');
    expect(
      q('analysisMemoryShortCircuit')!.querySelector('button')!.getAttribute('aria-checked'),
    ).toBe('true');
  });

  it('keeps the form disabled until settings arrive', async () => {
    await render(null);
    expect(section.form.disabled).toBe(true);
    expect(q<HTMLButtonElement>('save-analysis')!.disabled).toBe(true);
  });

  it('disables the cluster distance with a hint when no embedding model is chosen', async () => {
    await render(analysis(), '');
    expect(q<HTMLInputElement>('analysisClusterDistance')!.disabled).toBe(true);
    expect(q('analysisClusterDistance')!.closest('mat-form-field')!.textContent).toContain(
      'Choose an embedding model',
    );

    fixture.componentRef.setInput('embeddingModel', 'test-embed');
    await fixture.whenStable();
    expect(q<HTMLInputElement>('analysisClusterDistance')!.disabled).toBe(false);
  });

  it.each([
    ['analysisDefaultCount', '0', 'Enter a number from 1 to 1000.'],
    ['analysisDefaultCount', '1001', 'Enter a number from 1 to 1000.'],
    ['analysisBodyMaxChars', '499', 'Enter a number from 500 to 50000.'],
    ['analysisRepresentativesPerGroup', '1', 'Enter a number from 2 to 10.'],
    ['analysisMinGroupSize', '51', 'Enter a number from 2 to 50.'],
    ['analysisDerivedConfidencePenalty', '0.6', 'Enter a number from 0 to 0.5.'],
    ['analysisClusterDistance', '0.01', 'Enter a number from 0.02 to 0.6.'],
    ['analysisMemoryMinApprovals', '21', 'Enter a number from 1 to 20.'],
    ['analysisMinGroupSize', '2.5', 'Enter a whole number.'],
    ['analysisDefaultCount', '', 'Default analysis count is required.'],
  ])('%s = %j shows "%s" and does not emit', async (id, value, message) => {
    await render();
    await type(id, value);
    q('save-analysis')!.click();
    await fixture.whenStable();
    expect(fieldError(id)).toBe(message);
    expect(emitted).toEqual([]);
  });

  it('accepts the bounds themselves', async () => {
    await render();
    const limits = ANALYSIS_LIMITS.analysisRepresentativesPerGroup;
    await type('analysisRepresentativesPerGroup', String(limits.min));
    await type('analysisMinGroupSize', '50');
    q('save-analysis')!.click();
    await fixture.whenStable();
    expect(emitted).toEqual([{ analysisRepresentativesPerGroup: 2, analysisMinGroupSize: 50 }]);
  });

  it('emits only the changed fields', async () => {
    await render();
    await type('analysisBodyMaxChars', '8000');
    q('grouping-off')!.querySelector('input')!.click();
    q('analysisMemoryShortCircuit')!.querySelector('button')!.click();
    await fixture.whenStable();
    q('save-analysis')!.click();
    await fixture.whenStable();
    expect(emitted).toEqual([
      {
        analysisBodyMaxChars: 8000,
        analysisGroupingMode: 'off',
        analysisMemoryShortCircuit: false,
      },
    ]);
  });

  it('emits an empty object when nothing changed', async () => {
    await render();
    q('save-analysis')!.click();
    await fixture.whenStable();
    expect(emitted).toEqual([{}]);
  });

  it('never sends the cluster distance while it is disabled', async () => {
    await render(analysis(), '');
    section.c.analysisClusterDistance.setValue(0.3);
    expect(section.changes()).toEqual({});
  });

  describe('prompt field', () => {
    const builtIn = { version: 'test-v1', template: 'Built-in {{emails}}' };
    const field = () => q<HTMLTextAreaElement>('analysisPromptTemplate')!;
    const source = () => q('prompt-source')!.textContent!.trim();

    async function setDefault(prompt: typeof builtIn | null = builtIn) {
      fixture.componentRef.setInput('defaultPrompt', prompt);
      await fixture.whenStable();
    }

    function save() {
      q('save-analysis')!.click();
    }

    it('opens pre-filled with the built-in prompt when no override is saved', async () => {
      await render();
      await setDefault();
      expect(field().value).toBe('Built-in {{emails}}');
      expect(section.c.analysisPromptTemplate.pristine).toBe(true);
      expect(source()).toBe('Built-in prompt (test-v1)');
      expect(q('default-prompt-details')).toBeNull();
    });

    it('opens with the saved override', async () => {
      await render(analysis({ analysisPromptTemplate: 'Mine {{emails}}' }));
      await setDefault();
      expect(field().value).toBe('Mine {{emails}}');
      expect(source()).toBe('Custom prompt');
    });

    it('fills when the default arrives before the settings', async () => {
      await render(null);
      await setDefault();
      fixture.componentRef.setInput('settings', analysis());
      await fixture.whenStable();
      expect(field().value).toBe('Built-in {{emails}}');
      expect(section.c.analysisPromptTemplate.pristine).toBe(true);
    });

    it('a default arriving late never overwrites an edit', async () => {
      await render();
      await type('analysisPromptTemplate', 'Typed {{emails}}');
      await setDefault();
      expect(field().value).toBe('Typed {{emails}}');
      expect(source()).toBe('Custom prompt');
    });

    it('the status follows typing and goes back on Reset', async () => {
      await render();
      await setDefault();
      await type('analysisPromptTemplate', 'Built-in {{emails}} and more');
      expect(source()).toBe('Custom prompt');

      q('reset-prompt')!.click();
      await fixture.whenStable();
      expect(field().value).toBe('Built-in {{emails}}');
      expect(section.c.analysisPromptTemplate.dirty).toBe(true);
      expect(source()).toBe('Built-in prompt (test-v1)');
    });

    it('saving the unedited pre-fill sends nothing', async () => {
      await render();
      await setDefault();
      save();
      expect(emitted).toEqual([{}]);
    });

    it('saving after Reset on an override sends an empty string', async () => {
      await render(analysis({ analysisPromptTemplate: 'Mine {{emails}}' }));
      await setDefault();
      let resets = 0;
      section.resetPrompt.subscribe(() => resets++);
      q('reset-prompt')!.click();
      await fixture.whenStable();
      save();
      expect(resets).toBe(0);
      expect(field().value).toBe('Built-in {{emails}}');
      expect(emitted).toEqual([{ analysisPromptTemplate: '' }]);
    });

    it('without the built-in prompt, says so and Reset retries the load before filling', async () => {
      await render(analysis({ analysisPromptTemplate: 'Mine {{emails}}' }));
      expect(field().value).toBe('Mine {{emails}}');
      expect(source()).toBe('Built-in prompt (not loaded); an empty field uses it');

      let resets = 0;
      section.resetPrompt.subscribe(() => resets++);
      q('reset-prompt')!.click();
      await fixture.whenStable();
      expect(resets).toBe(1);
      expect(field().value).toBe('');

      await setDefault();
      expect(field().value).toBe('Built-in {{emails}}');
      expect(section.c.analysisPromptTemplate.dirty).toBe(true);
      expect(section.changes()).toEqual({ analysisPromptTemplate: '' });
    });

    it('stays empty with no override when the built-in prompt never loads', async () => {
      await render();
      expect(field().value).toBe('');
      expect(source()).toBe('Built-in prompt (not loaded); an empty field uses it');
      save();
      expect(emitted).toEqual([{}]);
    });
  });

  it('never saves a copy of the built-in prompt as an override', async () => {
    await render(analysis({ analysisPromptTemplate: 'Old {{emails}}' }));
    fixture.componentRef.setInput('defaultPrompt', {
      version: 'test-v1',
      template: 'New {{emails}}',
    });
    await type('analysisPromptTemplate', ' New {{emails}} ');
    expect(section.changes()).toEqual({ analysisPromptTemplate: '' });
  });

  it('Reset to default on the built-in prompt changes nothing', async () => {
    await render(analysis());
    q('reset-prompt')!.click();
    await fixture.whenStable();
    expect(section.changes()).toEqual({});
  });

  it('warns and blocks saving a prompt without {{emails}}', async () => {
    await render();
    expect(q('emails-warning')).toBeNull();
    await type('analysisPromptTemplate', 'No placeholder here');
    expect(q('emails-warning')!.textContent).toContain('{{emails}}');
    q('save-analysis')!.click();
    await fixture.whenStable();
    expect(emitted).toEqual([]);
  });

  it('clearing a custom prompt sends an empty string', async () => {
    await render(analysis({ analysisPromptTemplate: 'Old {{emails}}' }));
    await type('analysisPromptTemplate', '   ');
    expect(q('emails-warning')).toBeNull();
    expect(section.changes()).toEqual({ analysisPromptTemplate: '' });
  });

  it('shows server field errors on the matching control', async () => {
    await render();
    fixture.componentRef.setInput('serverErrors', {
      analysisPromptTemplate: ['Synthetic server error.'],
    });
    await fixture.whenStable();
    expect(fieldError('analysisPromptTemplate')).toBe('Synthetic server error.');
  });

  it('lists the prompt placeholders', async () => {
    await render();
    const names = [...el.querySelectorAll('dt code')].map((c) => c.textContent);
    expect(names).toContain('{{emails}}');
  });
});
