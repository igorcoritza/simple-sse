import { Injectable, OnDestroy, signal } from '@angular/core';
import { API_URL } from './api-url';

export interface ServerMessage {
  type: 'welcome' | 'tick' | 'custom';
  text: string;
  sentAt: string;
}

/** Progress of a Hangfire job, published by the worker through Redis and forwarded as SSE. */
export interface JobEvent {
  jobId: string;
  name: string;
  status: 'queued' | 'processing' | 'completed' | 'failed';
  progress: number;
  text: string;
  sentAt: string;
}

export type ConnectionStatus = 'connecting' | 'open' | 'closed';

/**
 * Wraps the browser's native EventSource API.
 * EventSource opens one long-lived HTTP request and the server pushes events through it.
 * If the connection drops, the browser reconnects on its own (using the `retry` value
 * the server sent).
 */
@Injectable({ providedIn: 'root' })
export class SseService implements OnDestroy {
  readonly messages = signal<ServerMessage[]>([]);
  readonly status = signal<ConnectionStatus>('closed');
  /** Latest state of each job, newest first. */
  readonly jobs = signal<JobEvent[]>([]);

  private source?: EventSource;

  connect(): void {
    if (this.source) return;

    this.status.set('connecting');
    this.source = new EventSource(`${API_URL}/api/events`);

    this.source.onopen = () => this.status.set('open');

    // Fires on network errors. readyState tells us whether the browser is retrying
    // (CONNECTING) or has given up (CLOSED).
    this.source.onerror = () =>
      this.status.set(this.source?.readyState === EventSource.CLOSED ? 'closed' : 'connecting');

    // The server sends named events ("event: tick"), so we listen to each name.
    // Unnamed events would arrive through `onmessage` instead.
    for (const eventName of ['welcome', 'tick', 'custom']) {
      this.source.addEventListener(eventName, (event) => {
        const message = JSON.parse((event as MessageEvent<string>).data) as ServerMessage;
        this.messages.update((list) => [message, ...list].slice(0, 50));
      });
    }

    // "job" events come from the Hangfire worker (via Redis pub/sub -> API -> SSE).
    this.source.addEventListener('job', (event) => {
      const job = JSON.parse((event as MessageEvent<string>).data) as JobEvent;
      this.jobs.update((list) => [job, ...list.filter((j) => j.jobId !== job.jobId)].slice(0, 10));
    });
  }

  disconnect(): void {
    this.source?.close();
    this.source = undefined;
    this.status.set('closed');
  }

  clear(): void {
    this.messages.set([]);
    this.jobs.set([]);
  }

  ngOnDestroy(): void {
    this.disconnect();
  }
}
