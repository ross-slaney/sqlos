import { Component, inject, OnInit, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { ApiService } from '../../../services/api.service';
import { PagedResponse, ChainDto } from '../../../models';

@Component({
  selector: 'app-chains',
  standalone: true,
  imports: [RouterLink, FormsModule],
  template: `
    <div class="gap-20">
      <div class="page-header">
        <h1>Chains</h1>
        <p>Retail chains visible to your account.</p>
      </div>

      @if (error()) {
        <p class="error">{{ error() }}</p>
      }

      <div class="card">
        <div class="toolbar">
          <input type="text" placeholder="Search chains..." [ngModel]="search()" (ngModelChange)="onSearch($event)" class="toolbar-search">
          <button type="button" class="secondary" (click)="showCreate.set(!showCreate())">
            {{ showCreate() ? 'Cancel' : '+ Add Chain' }}
          </button>
        </div>

        @if (showCreate()) {
          <form (ngSubmit)="handleCreate()" class="create-form">
            <input type="text" placeholder="Chain name" [(ngModel)]="newName" name="newName" required autofocus>
            <input type="text" placeholder="Description (optional)" [(ngModel)]="newDesc" name="newDesc">
            <input type="text" placeholder="Headquarters address (optional)" [(ngModel)]="newHq" name="newHq">
            <button type="submit" [disabled]="creating()">{{ creating() ? 'Creating...' : 'Create Chain' }}</button>
          </form>
        }

        @if (loading()) {
          <p class="muted" style="margin-top: 16px">Loading...</p>
        } @else if (chains().length === 0) {
          <div class="empty-state">
            <strong>No chains found</strong>
            <p>{{ search() ? 'Try a different search term.' : 'No chains visible with your current permissions.' }}</p>
          </div>
        } @else {
          <table class="data-table">
            <thead>
              <tr>
                <th>Name</th>
                <th>Description</th>
                <th class="text-right">Locations</th>
              </tr>
            </thead>
            <tbody>
              @for (c of chains(); track c.id) {
                <tr>
                  <td><a [routerLink]="'/retail/chains/' + c.id">{{ c.name }}</a></td>
                  <td class="muted">{{ c.description ?? '—' }}</td>
                  <td class="text-right"><span class="badge badge-neutral">{{ c.locationCount }}</span></td>
                </tr>
              }
            </tbody>
          </table>
        }
      </div>
    </div>
  `,
})
export class ChainsComponent implements OnInit {
  private api = inject(ApiService);

  chains = signal<ChainDto[]>([]);
  loading = signal(true);
  error = signal<string | null>(null);
  search = signal('');
  showCreate = signal(false);
  creating = signal(false);

  newName = '';
  newDesc = '';
  newHq = '';

  ngOnInit() {
    this.loadChains();
  }

  onSearch(value: string) {
    this.search.set(value);
    this.loadChains();
  }

  async loadChains() {
    this.loading.set(true);
    const params = new URLSearchParams({ pageSize: '50' });
    if (this.search()) params.set('search', this.search());
    try {
      const r = await this.api.get<PagedResponse<ChainDto>>(`/api/chains?${params}`);
      this.chains.set(r.data);
    } catch (e: any) {
      this.error.set(e.message);
    } finally {
      this.loading.set(false);
    }
  }

  async handleCreate() {
    if (!this.newName.trim()) return;
    this.creating.set(true);
    this.error.set(null);
    try {
      await this.api.post('/api/chains', {
        name: this.newName.trim(),
        description: this.newDesc.trim() || null,
        headquartersAddress: this.newHq.trim() || null,
      });
      this.newName = ''; this.newDesc = ''; this.newHq = '';
      this.showCreate.set(false);
      await this.loadChains();
    } catch (e: any) {
      this.error.set(e.message);
    } finally {
      this.creating.set(false);
    }
  }
}
