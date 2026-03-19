import { Component, inject, OnInit, signal } from '@angular/core';
import { jwtDecode } from 'jwt-decode';
import { environment } from '../../environments/environment';
import { AuthService } from '../../services/auth.service';
import { DemoSubject } from '../../models';

function humanizeRole(raw: string): string {
  return raw
    .replace(/([a-z])([A-Z])/g, '$1 $2')
    .replace(/_/g, ' ')
    .replace(/\b\w/g, (c) => c.toUpperCase())
    .trim();
}

function formatLabel(s: DemoSubject): string {
  const name = s.displayName;
  if (s.type === 'agent') return `${name} (Agent)`;
  if (s.type === 'service_account') return `${name} (API)`;

  const role = s.role;
  if (!role) return name;

  const parts = role
    .split(',')
    .map((r) => r.trim())
    .filter((r) => r && !/^org_(admin|member)$/i.test(r));

  if (parts.length === 0) return name;

  const humanized = parts.map(humanizeRole).join(', ');
  const nameLower = name.toLowerCase();
  if (humanized.toLowerCase().split(' ').every((w) => nameLower.includes(w))) return name;

  return `${name} · ${humanized}`;
}

function selectKey(subject: DemoSubject): string {
  if (subject.email) return subject.email;
  if (subject.credential) return `${subject.type}:${subject.credential}`;
  return `${subject.type}:${subject.displayName}`;
}

@Component({
  selector: 'app-user-switcher',
  standalone: true,
  template: `
    <div class="user-switcher">
      <select
        [value]="selectedKey()"
        (change)="handleSwitch($event)"
        [disabled]="switching() || subjects().length === 0"
        title="Switch demo identity">
        @if (!selectedKey()) {
          <option value="">Switch identity...</option>
        }
        @for (s of subjects(); track selectKey(s)) {
          <option [value]="selectKey(s)">{{ formatLabel(s) }}</option>
        }
      </select>
    </div>
  `,
})
export class UserSwitcherComponent implements OnInit {
  private auth = inject(AuthService);

  subjects = signal<DemoSubject[]>([]);
  switching = signal(false);

  selectKey = selectKey;
  formatLabel = formatLabel;

  selectedKey = signal('');

  ngOnInit() {
    fetch(`${environment.apiUrl}/api/demo/users`)
      .then((r) => r.json())
      .then((data: DemoSubject[]) => {
        this.subjects.set(data);
        this.computeSelectedKey();
      })
      .catch(() => {});
  }

  private computeSelectedKey() {
    const currentEmail = this.auth.user()?.email;
    const activeOverride = this.auth.getAuthOverride();
    const currentKey = activeOverride
      ? `${activeOverride.type}:${activeOverride.value}`
      : (currentEmail ?? '');
    const found = this.subjects().some((s) => selectKey(s) === currentKey);
    this.selectedKey.set(found ? currentKey : '');
  }

  async handleSwitch(event: Event) {
    const key = (event.target as HTMLSelectElement).value;
    if (this.switching() || !key) return;

    const subject = this.subjects().find((s) => selectKey(s) === key);
    if (!subject) return;

    this.switching.set(true);
    try {
      if (subject.type === 'agent' && subject.credential) {
        this.auth.setAuthOverride({
          type: 'agent',
          header: 'X-Agent-Token',
          value: subject.credential,
          displayName: subject.displayName,
        });
        window.location.reload();
        return;
      }

      if (subject.type === 'service_account' && subject.credential) {
        this.auth.setAuthOverride({
          type: 'service_account',
          header: 'X-Api-Key',
          value: subject.credential,
          displayName: subject.displayName,
        });
        window.location.reload();
        return;
      }

      if (subject.type === 'user' && subject.email) {
        this.auth.setAuthOverride(null);

        const res = await fetch(`${environment.apiUrl}/api/v1/auth/demo/switch`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ email: subject.email }),
        });
        if (!res.ok) throw new Error('Switch failed');

        const data = await res.json();
        const decoded = jwtDecode<{ sub?: string; exp: number; org_id?: string }>(data.accessToken);

        this.auth.setSession({
          accessToken: data.accessToken,
          refreshToken: data.refreshToken,
          userId: decoded.sub ?? '',
          displayName: subject.displayName,
          email: subject.email,
          organizationId: data.organizationId ?? decoded.org_id ?? null,
          sessionId: data.sessionId,
          exp: decoded.exp,
        });
        window.location.reload();
      }
    } catch {
      // Ignore - the user can try again
    } finally {
      this.switching.set(false);
    }
  }
}
