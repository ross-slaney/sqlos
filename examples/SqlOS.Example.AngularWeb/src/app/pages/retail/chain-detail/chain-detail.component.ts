import { Component, inject, OnInit, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { ApiService } from '../../../services/api.service';
import { ChainDetail, LocationDto, PagedResponse } from '../../../models';

@Component({
  selector: 'app-chain-detail',
  standalone: true,
  imports: [RouterLink, FormsModule],
  template: `
    @if (loading()) {
      <div class="gap-20"><p class="muted">Loading...</p></div>
    } @else if (error() && !chain()) {
      <div class="gap-20">
        <div class="empty-state">
          <strong>{{ error()!.includes('403') ? 'Access Denied' : 'Error' }}</strong>
          <p>{{ error()!.includes('403') ? "You don't have permission to view this chain." : error() }}</p>
        </div>
      </div>
    } @else if (!chain()) {
      <div class="gap-20"><p class="muted">Chain not found.</p></div>
    } @else {
      <div class="gap-20">
        <nav class="breadcrumb">
          <a routerLink="/retail/chains">Chains</a>
          <span>/</span>
          <span style="color: var(--color-text)">{{ chain()!.name }}</span>
        </nav>

        @if (error()) {
          <p class="error">{{ error() }}</p>
        }

        <div class="card">
          @if (editing()) {
            <form (ngSubmit)="handleSave()" class="create-form" style="margin: 0">
              <h2>Edit Chain</h2>
              <input type="text" placeholder="Chain name" [(ngModel)]="editName" name="editName" required>
              <input type="text" placeholder="Description" [(ngModel)]="editDesc" name="editDesc">
              <input type="text" placeholder="HQ Address" [(ngModel)]="editHq" name="editHq">
              <div class="actions" style="margin-top: 4px">
                <button type="submit" [disabled]="saving()">{{ saving() ? 'Saving...' : 'Save' }}</button>
                <button type="button" class="secondary" (click)="editing.set(false)">Cancel</button>
              </div>
            </form>
          } @else {
            <div class="card-header">
              <div>
                <h2 style="margin-bottom: 0">{{ chain()!.name }}</h2>
                @if (chain()!.description) {
                  <p class="muted" style="font-size: 13px; margin-top: 2px">{{ chain()!.description }}</p>
                }
                <p class="muted" style="font-size: 12px; margin-top: 4px">
                  {{ chain()!.locationCount }} location{{ chain()!.locationCount !== 1 ? 's' : '' }}
                  @if (chain()!.headquartersAddress) {
                    &nbsp;· HQ: {{ chain()!.headquartersAddress }}
                  }
                </p>
              </div>
              <div class="card-header-actions">
                <button type="button" class="secondary sm" (click)="editing.set(true)">Edit</button>
                <button type="button" class="danger sm" (click)="handleDelete()">Delete</button>
              </div>
            </div>
          }
        </div>

        <div class="card">
          <div class="toolbar">
            <h3>Locations</h3>
            <button type="button" class="secondary sm" (click)="showAddLoc.set(!showAddLoc())">
              {{ showAddLoc() ? 'Cancel' : '+ Add Location' }}
            </button>
          </div>

          @if (showAddLoc()) {
            <form (ngSubmit)="handleAddLocation()" class="create-form">
              <div class="form-row">
                <input type="text" placeholder="Location name" [(ngModel)]="newLocName" name="newLocName" required autofocus>
                <input type="text" placeholder="Store number" [(ngModel)]="newLocNumber" name="newLocNumber">
              </div>
              <input type="text" placeholder="Address" [(ngModel)]="newLocAddress" name="newLocAddress">
              <div class="form-row">
                <input type="text" placeholder="City" [(ngModel)]="newLocCity" name="newLocCity">
                <input type="text" placeholder="State" [(ngModel)]="newLocState" name="newLocState">
                <input type="text" placeholder="Zip" [(ngModel)]="newLocZip" name="newLocZip">
              </div>
              <button type="submit" [disabled]="addingLoc()">{{ addingLoc() ? 'Adding...' : 'Add Location' }}</button>
            </form>
          }

          @if (locations().length === 0) {
            <div class="empty-state"><strong>No locations</strong><p>Add store locations to this chain.</p></div>
          } @else {
            <table class="data-table">
              <thead><tr><th>Name</th><th>Store #</th><th>City</th><th>State</th></tr></thead>
              <tbody>
                @for (loc of locations(); track loc.id) {
                  <tr>
                    <td><a [routerLink]="'/retail/locations/' + loc.id">{{ loc.name }}</a></td>
                    <td class="mono">{{ loc.storeNumber ?? '—' }}</td>
                    <td>{{ loc.city ?? '—' }}</td>
                    <td>{{ loc.state ?? '—' }}</td>
                  </tr>
                }
              </tbody>
            </table>
          }
        </div>
      </div>
    }
  `,
})
export class ChainDetailComponent implements OnInit {
  private api = inject(ApiService);
  private route = inject(ActivatedRoute);

  chain = signal<ChainDetail | null>(null);
  locations = signal<LocationDto[]>([]);
  loading = signal(true);
  error = signal<string | null>(null);
  editing = signal(false);
  saving = signal(false);
  showAddLoc = signal(false);
  addingLoc = signal(false);

  editName = '';
  editDesc = '';
  editHq = '';
  newLocName = '';
  newLocNumber = '';
  newLocAddress = '';
  newLocCity = '';
  newLocState = '';
  newLocZip = '';

  private get chainId(): string {
    return this.route.snapshot.paramMap.get('chainId')!;
  }

  ngOnInit() {
    this.loadData();
  }

  async loadData() {
    this.loading.set(true);
    this.error.set(null);
    try {
      const [c, locs] = await Promise.all([
        this.api.get<ChainDetail>(`/api/chains/${this.chainId}`),
        this.api.get<PagedResponse<LocationDto>>(`/api/chains/${this.chainId}/locations?pageSize=50`),
      ]);
      this.chain.set(c);
      this.locations.set(locs.data);
      this.editName = c.name;
      this.editDesc = c.description ?? '';
      this.editHq = c.headquartersAddress ?? '';
    } catch (e: any) {
      this.error.set(e.message);
    } finally {
      this.loading.set(false);
    }
  }

  async handleSave() {
    const c = this.chain();
    if (!c) return;
    this.saving.set(true); this.error.set(null);
    try {
      const updated = await this.api.put<ChainDetail>(`/api/chains/${c.id}`, {
        name: this.editName.trim(),
        description: this.editDesc.trim() || null,
        headquartersAddress: this.editHq.trim() || null,
      });
      this.chain.set(updated);
      this.editing.set(false);
    } catch (e: any) {
      this.error.set(e.message);
    } finally {
      this.saving.set(false);
    }
  }

  async handleDelete() {
    const c = this.chain();
    if (!c || !confirm(`Delete ${c.name}? This cannot be undone.`)) return;
    try {
      await this.api.delete(`/api/chains/${c.id}`);
      window.location.href = '/retail/chains';
    } catch (e: any) {
      this.error.set(e.message);
    }
  }

  async handleAddLocation() {
    if (!this.newLocName.trim()) return;
    this.addingLoc.set(true); this.error.set(null);
    try {
      await this.api.post(`/api/chains/${this.chainId}/locations`, {
        name: this.newLocName.trim(),
        storeNumber: this.newLocNumber.trim() || null,
        address: this.newLocAddress.trim() || null,
        city: this.newLocCity.trim() || null,
        state: this.newLocState.trim() || null,
        zipCode: this.newLocZip.trim() || null,
      });
      this.newLocName = ''; this.newLocNumber = ''; this.newLocAddress = '';
      this.newLocCity = ''; this.newLocState = ''; this.newLocZip = '';
      this.showAddLoc.set(false);
      await this.loadData();
    } catch (e: any) {
      this.error.set(e.message);
    } finally {
      this.addingLoc.set(false);
    }
  }
}
