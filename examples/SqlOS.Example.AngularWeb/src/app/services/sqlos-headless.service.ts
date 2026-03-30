import { Injectable, inject } from '@angular/core';
import { SqlosAuthService } from './sqlos-auth.service';
import { HeadlessActionResult, HeadlessViewModel } from '../models';

@Injectable({ providedIn: 'root' })
export class SqlosHeadlessService {
  private sqlosAuth = inject(SqlosAuthService);

  private get headlessBase(): string {
    return `${this.sqlosAuth.getAuthServerUrl()}/headless`;
  }

  private async headlessPost(path: string, body: unknown): Promise<HeadlessActionResult> {
    const res = await fetch(`${this.headlessBase}${path}`, {
      method: 'POST',
      credentials: 'include',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    });
    if (!res.ok) {
      const text = await res.text();
      throw new Error(text || `Headless API error: ${res.status}`);
    }
    return res.json();
  }

  async getHeadlessRequest(
    requestId: string,
    view?: string,
    error?: string | null,
    pendingToken?: string | null,
    email?: string | null,
    displayName?: string | null,
  ): Promise<HeadlessViewModel> {
    const url = new URL(`${this.headlessBase}/requests/${requestId}`);
    if (view) url.searchParams.set('view', view);
    if (error) url.searchParams.set('error', error);
    if (pendingToken) url.searchParams.set('pendingToken', pendingToken);
    if (email) url.searchParams.set('email', email);
    if (displayName) url.searchParams.set('displayName', displayName);

    const res = await fetch(url.toString(), {
      credentials: 'include',
    });
    if (!res.ok) {
      const text = await res.text();
      throw new Error(text || `Failed to load request: ${res.status}`);
    }
    return res.json();
  }

  identify(requestId: string, email: string): Promise<HeadlessActionResult> {
    return this.headlessPost('/identify', { requestId, email });
  }

  passwordLogin(requestId: string, email: string, password: string): Promise<HeadlessActionResult> {
    return this.headlessPost('/password/login', { requestId, email, password });
  }

  signup(
    requestId: string,
    displayName: string,
    email: string,
    password: string,
    organizationName: string,
    customFields?: Record<string, string>,
  ): Promise<HeadlessActionResult> {
    return this.headlessPost('/signup', {
      requestId,
      displayName,
      email,
      password,
      organizationName,
      customFields: customFields ?? {},
    });
  }

  selectOrganization(pendingToken: string, organizationId: string): Promise<HeadlessActionResult> {
    return this.headlessPost('/organization/select', { pendingToken, organizationId });
  }

  startProvider(requestId: string, connectionId: string, email?: string): Promise<HeadlessActionResult> {
    return this.headlessPost('/provider/start', { requestId, connectionId, email });
  }
}
