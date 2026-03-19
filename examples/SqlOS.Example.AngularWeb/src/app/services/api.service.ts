import { Injectable, inject } from '@angular/core';
import { environment } from '../environments/environment';
import { AuthService } from './auth.service';

@Injectable({ providedIn: 'root' })
export class ApiService {
  private auth = inject(AuthService);

  private async apiFetch(path: string, init?: RequestInit): Promise<Response> {
    const token = await this.auth.ensureValidToken();
    const override = this.auth.getAuthOverride();
    const authHeaders: Record<string, string> = override
      ? { [override.header]: override.value }
      : token
        ? { Authorization: `Bearer ${token}` }
        : {};

    return fetch(`${environment.apiUrl}${path}`, {
      ...init,
      headers: {
        'Content-Type': 'application/json',
        ...authHeaders,
        ...init?.headers,
      },
      cache: 'no-store',
    });
  }

  async get<T = unknown>(path: string): Promise<T> {
    const response = await this.apiFetch(path);
    if (!response.ok) {
      throw new Error(`GET ${path} failed with ${response.status}`);
    }
    return response.json();
  }

  async post<T = unknown>(path: string, body: unknown): Promise<T> {
    const response = await this.apiFetch(path, {
      method: 'POST',
      body: JSON.stringify(body),
    });
    if (!response.ok) {
      throw new Error(`POST ${path} failed with ${response.status}`);
    }
    return response.json();
  }

  async put<T = unknown>(path: string, body: unknown): Promise<T> {
    const response = await this.apiFetch(path, {
      method: 'PUT',
      body: JSON.stringify(body),
    });
    if (!response.ok) {
      throw new Error(`PUT ${path} failed with ${response.status}`);
    }
    return response.json();
  }

  async delete(path: string): Promise<void> {
    const response = await this.apiFetch(path, { method: 'DELETE' });
    if (!response.ok) {
      throw new Error(`DELETE ${path} failed with ${response.status}`);
    }
  }
}
