import { Component, inject, OnInit, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { HttpClient } from '@angular/common/http';
import { FormsModule } from '@angular/forms';
import { API_URL } from './api-url';
import { SseService } from './sse.service';

@Component({
  selector: 'app-root',
  imports: [DatePipe, FormsModule],
  templateUrl: './app.html',
  styleUrl: './app.css',
})
export class App implements OnInit {
  protected readonly sse = inject(SseService);
  private readonly http = inject(HttpClient);

  protected readonly draft = signal('');
  protected readonly sending = signal(false);

  protected readonly jobName = signal('Monthly report');
  protected readonly jobSteps = signal(5);

  ngOnInit(): void {
    this.sse.connect();
  }

  // Sending uses a normal POST: SSE is one-way (server -> client).
  // The server then pushes the message back to every connected client through the stream.
  protected send(): void {
    const text = this.draft().trim();
    if (!text) return;

    this.sending.set(true);
    this.http.post(`${API_URL}/api/messages`, { text }).subscribe({
      next: () => this.draft.set(''),
      complete: () => this.sending.set(false),
      error: () => this.sending.set(false),
    });
  }

  // Starts a Hangfire job. The API answers immediately; progress arrives later via SSE.
  protected startJob(): void {
    this.http
      .post(`${API_URL}/api/jobs`, { name: this.jobName(), steps: this.jobSteps() })
      .subscribe();
  }
}
