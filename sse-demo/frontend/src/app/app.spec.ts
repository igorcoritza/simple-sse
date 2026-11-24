import { TestBed } from '@angular/core/testing';
import { App } from './app';
import { SseService } from './sse.service';
import { provideHttpClient } from '@angular/common/http';

describe('App', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [App],
      providers: [
        provideHttpClient(),
        // Avoid opening a real EventSource in unit tests.
        { provide: SseService, useValue: { messages: () => [], jobs: () => [], status: () => 'closed', connect() {}, disconnect() {}, clear() {} } },
      ],
    }).compileComponents();
  });

  it('should render the title', async () => {
    const fixture = TestBed.createComponent(App);
    await fixture.whenStable();
    const compiled = fixture.nativeElement as HTMLElement;
    expect(compiled.querySelector('h1')?.textContent).toContain('Server-Sent Events');
  });
});
