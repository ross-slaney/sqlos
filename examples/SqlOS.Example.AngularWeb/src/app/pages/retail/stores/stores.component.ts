import { Component, inject, OnInit, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { ApiService } from '../../../services/api.service';
import { PagedResponse, LocationDto } from '../../../models';

@Component({
  selector: 'app-stores',
  standalone: true,
  imports: [RouterLink, FormsModule],
  template: `
    <div class="gap-20">
      <div class="page-header">
        <h1>Stores</h1>
        <p>All store locations visible to your account.</p>
      </div>

      @if (error()) {
        <p class="error">{{ error() }}</p>
      }

      <div class="card">
        <div class="toolbar">
          <input type="text" placeholder="Search stores..." [ngModel]="search()" (ngModelChange)="onSearch($event)" class="toolbar-search">
          <span class="badge badge-neutral">{{ loading() ? '...' : stores().length + ' store' + (stores().length !== 1 ? 's' : '') }}</span>
        </div>

        @if (loading()) {
          <p class="muted" style="margin-top: 16px">Loading...</p>
        } @else if (stores().length === 0) {
          <div class="empty-state">
            <strong>No stores found</strong>
            <p>{{ search() ? 'Try a different search term.' : 'No stores visible with your current permissions.' }}</p>
          </div>
        } @else {
          <table class="data-table">
            <thead>
              <tr>
                <th>Name</th>
                <th>Store #</th>
                <th>Chain</th>
                <th>City</th>
                <th>State</th>
              </tr>
            </thead>
            <tbody>
              @for (s of stores(); track s.id) {
                <tr>
                  <td><a [routerLink]="'/retail/locations/' + s.id">{{ s.name }}</a></td>
                  <td class="mono">{{ s.storeNumber ?? '—' }}</td>
                  <td><a [routerLink]="'/retail/chains/' + s.chainId" class="muted">{{ s.chainName ?? s.chainId }}</a></td>
                  <td>{{ s.city ?? '—' }}</td>
                  <td>{{ s.state ?? '—' }}</td>
                </tr>
              }
            </tbody>
          </table>
        }
      </div>
    </div>
  `,
})
export class StoresComponent implements OnInit {
  private api = inject(ApiService);

  stores = signal<LocationDto[]>([]);
  loading = signal(true);
  error = signal<string | null>(null);
  search = signal('');

  ngOnInit() {
    this.loadStores();
  }

  onSearch(value: string) {
    this.search.set(value);
    this.loadStores();
  }

  async loadStores() {
    this.loading.set(true);
    const params = new URLSearchParams({ pageSize: '50' });
    if (this.search()) params.set('search', this.search());
    try {
      const r = await this.api.get<PagedResponse<LocationDto>>(`/api/locations?${params}`);
      this.stores.set(r.data);
    } catch (e: any) {
      this.error.set(e.message);
    } finally {
      this.loading.set(false);
    }
  }
}
