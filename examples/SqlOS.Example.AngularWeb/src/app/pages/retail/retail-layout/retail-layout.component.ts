import { Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { SidebarComponent } from '../../../components/sidebar/sidebar.component';

@Component({
  selector: 'app-retail-layout',
  standalone: true,
  imports: [RouterOutlet, SidebarComponent],
  template: `
    <app-sidebar />
    <main class="app-shell">
      <div class="page-container">
        <router-outlet />
      </div>
    </main>
  `,
})
export class RetailLayoutComponent {}
