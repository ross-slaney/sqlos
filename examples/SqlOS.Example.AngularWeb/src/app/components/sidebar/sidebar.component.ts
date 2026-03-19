import { Component, inject, computed } from '@angular/core';
import { RouterLink, Router } from '@angular/router';
import { AuthService } from '../../services/auth.service';
import { UserSwitcherComponent } from '../user-switcher/user-switcher.component';

@Component({
  selector: 'app-sidebar',
  standalone: true,
  imports: [RouterLink, UserSwitcherComponent],
  template: `
    <aside class="sidebar">
      <div class="sidebar-top">
        <a routerLink="/retail" class="sidebar-brand">
          <div class="sidebar-brand-icon">N</div>
          <span>Northwind Retail</span>
        </a>

        <nav class="sidebar-nav">
          @for (link of links; track link.href) {
            <a [routerLink]="link.href"
               class="sidebar-link"
               [class.active]="isActive(link.href, link.exact)">
              <span [innerHTML]="link.icon"></span>
              <span>{{ link.label }}</span>
            </a>
          }
        </nav>

        <div class="sidebar-section-label">
          <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
            <path d="M20 13c0 5-3.5 7.5-7.66 8.95a1 1 0 0 1-.67-.01C7.5 20.5 4 18 4 13V6a1 1 0 0 1 1-1c2 0 4.5-1.2 6.24-2.72a1.17 1.17 0 0 1 1.52 0C14.51 3.81 17 5 19 5a1 1 0 0 1 1 1z" />
          </svg>
          <span>FGA Demo</span>
        </div>
        <div class="sidebar-fga-note">
          Data is filtered by your grants. Switch identities to see different views.
        </div>
      </div>

      <div class="sidebar-bottom">
        <app-user-switcher />
        <div class="sidebar-user-row">
          @if (userName()) {
            <span class="sidebar-user-name">{{ userName() }}</span>
          }
          <button type="button" class="logout-btn" (click)="signOut()">Sign out</button>
        </div>
      </div>
    </aside>
  `,
})
export class SidebarComponent {
  private auth = inject(AuthService);
  private router = inject(Router);

  userName = computed(() => this.auth.user()?.name ?? null);

  links = [
    {
      href: '/retail',
      label: 'Dashboard',
      exact: true,
      icon: '<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="3" width="7" height="7" rx="1"></rect><rect x="14" y="3" width="7" height="7" rx="1"></rect><rect x="3" y="14" width="7" height="7" rx="1"></rect><rect x="14" y="14" width="7" height="7" rx="1"></rect></svg>',
    },
    {
      href: '/retail/chains',
      label: 'Chains',
      exact: false,
      icon: '<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M6 3h12l4 6-10 13L2 9Z"></path><path d="M11 3 8 9l4 13 4-13-3-6"></path><path d="M2 9h20"></path></svg>',
    },
    {
      href: '/retail/stores',
      label: 'Stores',
      exact: false,
      icon: '<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="m2 7 4.41-4.41A2 2 0 0 1 7.83 2h8.34a2 2 0 0 1 1.42.59L22 7"></path><path d="M4 12v8a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2v-8"></path><path d="M15 22v-4a2 2 0 0 0-2-2h-2a2 2 0 0 0-2 2v4"></path><path d="M2 7h20"></path><path d="M22 7v3a2 2 0 0 1-2 2a2.7 2.7 0 0 1-1.59-.63.7.7 0 0 0-.82 0A2.7 2.7 0 0 1 16 12a2.7 2.7 0 0 1-1.59-.63.7.7 0 0 0-.82 0A2.7 2.7 0 0 1 12 12a2.7 2.7 0 0 1-1.59-.63.7.7 0 0 0-.82 0A2.7 2.7 0 0 1 8 12a2.7 2.7 0 0 1-1.59-.63.7.7 0 0 0-.82 0A2.7 2.7 0 0 1 4 12a2 2 0 0 1-2-2V7"></path></svg>',
    },
  ];

  isActive(href: string, exact: boolean): boolean {
    const url = this.router.url.split('?')[0];
    return exact ? url === href : url.startsWith(href);
  }

  signOut() {
    void this.auth.signOut('/');
  }
}
