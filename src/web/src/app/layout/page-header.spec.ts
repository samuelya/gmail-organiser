import { TestBed } from '@angular/core/testing';
import { PageHeader } from './page-header';

describe('PageHeader', () => {
  async function render(title: string, description?: string) {
    const fixture = TestBed.createComponent(PageHeader);
    fixture.componentRef.setInput('title', title);
    if (description !== undefined) fixture.componentRef.setInput('description', description);
    await fixture.whenStable();
    return fixture.nativeElement as HTMLElement;
  }

  it('renders only the title when no description is given', async () => {
    const el = await render('Example page');
    expect(el.querySelector('h1')?.textContent?.trim()).toBe('Example page');
    expect(el.querySelector('[data-testid="page-description"]')).toBeNull();
    expect(el.querySelectorAll('p').length).toBe(0);
  });

  it('shows the description as one paragraph under the single heading', async () => {
    const el = await render('Example page', 'What this example page is for.');
    expect(el.querySelectorAll('h1, h2, h3, h4, h5, h6').length).toBe(1);
    const description = el.querySelector('[data-testid="page-description"]');
    expect(description?.tagName).toBe('P');
    expect(description?.textContent?.trim()).toBe('What this example page is for.');
  });
});
