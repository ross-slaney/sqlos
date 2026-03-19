import { Component, inject, OnInit, signal, computed } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { ApiService } from '../../../services/api.service';
import { LocationDetail, InventoryItemDto, PagedResponse } from '../../../models';

function stockLevel(qty: number) {
  if (qty === 0) return 'out';
  if (qty <= 10) return 'low';
  return 'ok';
}

function stockLabel(qty: number) {
  if (qty === 0) return 'Out of stock';
  if (qty <= 10) return 'Low stock';
  return 'In stock';
}

@Component({
  selector: 'app-location-detail',
  standalone: true,
  imports: [RouterLink, FormsModule],
  template: `
    @if (loading()) {
      <div class="gap-20"><p class="muted">Loading...</p></div>
    } @else if (error() && !location()) {
      <div class="gap-20">
        <div class="empty-state">
          <strong>{{ error()!.includes('403') ? 'Access Denied' : 'Error' }}</strong>
          <p>{{ error()!.includes('403') ? "You don't have permission to view this location. Your current role may not include access to this store." : error() }}</p>
        </div>
      </div>
    } @else if (!location()) {
      <div class="gap-20"><p class="muted">Location not found.</p></div>
    } @else {
      <div class="gap-20">
        <nav class="breadcrumb">
          <a routerLink="/retail/chains">Chains</a>
          <span>/</span>
          <a [routerLink]="'/retail/chains/' + location()!.chainId">{{ location()!.chainName ?? 'Chain' }}</a>
          <span>/</span>
          <span style="color: var(--color-text)">{{ location()!.name }}</span>
        </nav>

        @if (error()) {
          <p class="error">{{ error() }}</p>
        }

        <div class="card">
          @if (editing()) {
            <form (ngSubmit)="handleSaveLocation()" class="create-form" style="margin: 0">
              <h2>Edit Location</h2>
              <input type="text" placeholder="Store name" [(ngModel)]="editName" name="editName" required>
              <input type="text" placeholder="Store number" [(ngModel)]="editNumber" name="editNumber">
              <input type="text" placeholder="Address" [(ngModel)]="editAddr" name="editAddr">
              <div class="form-row">
                <input type="text" placeholder="City" [(ngModel)]="editCity" name="editCity">
                <input type="text" placeholder="State" [(ngModel)]="editState" name="editState">
                <input type="text" placeholder="Zip" [(ngModel)]="editZip" name="editZip">
              </div>
              <div class="actions" style="margin-top: 4px">
                <button type="submit" [disabled]="saving()">{{ saving() ? 'Saving...' : 'Save' }}</button>
                <button type="button" class="secondary" (click)="editing.set(false)">Cancel</button>
              </div>
            </form>
          } @else {
            <div class="card-header">
              <div>
                <h2 style="margin-bottom: 0">{{ location()!.name }}</h2>
                <p class="muted" style="font-size: 13px; margin-top: 2px">
                  @if (location()!.storeNumber) {
                    <span class="mono">#{{ location()!.storeNumber }}</span> ·&nbsp;
                  }
                  {{ addressParts().length > 0 ? addressParts().join(', ') : 'No address on file' }}
                </p>
              </div>
              <div class="card-header-actions">
                <button type="button" class="secondary sm" (click)="editing.set(true)">Edit</button>
                <button type="button" class="danger sm" (click)="handleDeleteLocation()">Delete</button>
              </div>
            </div>
            <div class="detail-grid">
              <div class="detail-item">
                <span>Items</span>
                <strong>{{ inventory().length }}</strong>
              </div>
              <div class="detail-item">
                <span>Inventory Value</span>
                <strong>\${{ totalValue().toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 }) }}</strong>
              </div>
            </div>
          }
        </div>

        <div class="card">
          <div class="toolbar">
            <h3>Inventory</h3>
            <button type="button" class="secondary sm" (click)="showAddItem.set(!showAddItem())">
              {{ showAddItem() ? 'Cancel' : '+ Add Item' }}
            </button>
          </div>

          @if (showAddItem()) {
            <form (ngSubmit)="handleAddItem()" class="create-form">
              <div class="form-row">
                <input type="text" placeholder="SKU" [(ngModel)]="newSku" name="newSku" required>
                <input type="text" placeholder="Item name" [(ngModel)]="newItemName" name="newItemName" required>
              </div>
              <input type="text" placeholder="Description (optional)" [(ngModel)]="newItemDesc" name="newItemDesc">
              <div class="form-row">
                <input type="number" step="0.01" placeholder="Price" [(ngModel)]="newPrice" name="newPrice" required>
                <input type="number" placeholder="Quantity" [(ngModel)]="newQty" name="newQty" required>
              </div>
              <button type="submit" [disabled]="addingItem()">{{ addingItem() ? 'Adding...' : 'Add Item' }}</button>
            </form>
          }

          @if (inventory().length === 0) {
            <div class="empty-state">
              <strong>No inventory</strong>
              <p>Add items to start tracking inventory at this location.</p>
            </div>
          } @else {
            <table class="data-table">
              <thead>
                <tr>
                  <th>SKU</th>
                  <th>Name</th>
                  <th class="text-right">Price</th>
                  <th>Stock</th>
                  <th>Status</th>
                  <th></th>
                </tr>
              </thead>
              <tbody>
                @for (item of inventory(); track item.id) {
                  <tr>
                    @if (editingItemId() === item.id) {
                      <td class="mono">{{ item.sku }}</td>
                      <td><input type="text" [(ngModel)]="editItemName" class="inline-input"></td>
                      <td><input type="number" step="0.01" [(ngModel)]="editItemPrice" class="inline-input"></td>
                      <td><input type="number" [(ngModel)]="editItemQty" class="inline-input"></td>
                      <td></td>
                      <td class="row-actions">
                        <button type="button" class="sm" (click)="handleSaveItem(item.id)">Save</button>
                        <button type="button" class="secondary sm" (click)="editingItemId.set(null)">Cancel</button>
                      </td>
                    } @else {
                      <td class="mono">{{ item.sku }}</td>
                      <td style="font-weight: 500">{{ item.name }}</td>
                      <td class="text-right" style="font-variant-numeric: tabular-nums">\${{ item.price.toFixed(2) }}</td>
                      <td>
                        <div class="stock-bar-container">
                          <span class="stock-qty" [class]="stockLevel(item.quantityOnHand)">{{ item.quantityOnHand }}</span>
                          <div class="stock-bar">
                            <div class="stock-bar-fill" [class]="stockLevel(item.quantityOnHand)" [style.width.%]="Math.min((item.quantityOnHand / maxQty()) * 100, 100)"></div>
                          </div>
                        </div>
                      </td>
                      <td>
                        <span class="badge" [class.badge-success]="stockLevel(item.quantityOnHand) === 'ok'" [class.badge-warning]="stockLevel(item.quantityOnHand) === 'low'" [class.badge-danger]="stockLevel(item.quantityOnHand) === 'out'">
                          {{ stockLabel(item.quantityOnHand) }}
                        </span>
                      </td>
                      <td class="row-actions">
                        @if (item.quantityOnHand <= 10) {
                          <button type="button" class="secondary sm" (click)="handleRestock(item)" title="Restock +50">Restock</button>
                        }
                        <button type="button" class="secondary sm" (click)="startEditItem(item)">Edit</button>
                        <button type="button" class="danger sm" (click)="handleDeleteItem(item.id, item.name)">Delete</button>
                      </td>
                    }
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
export class LocationDetailComponent implements OnInit {
  private api = inject(ApiService);
  private route = inject(ActivatedRoute);

  Math = Math;
  stockLevel = stockLevel;
  stockLabel = stockLabel;

  location = signal<LocationDetail | null>(null);
  inventory = signal<InventoryItemDto[]>([]);
  loading = signal(true);
  error = signal<string | null>(null);
  editing = signal(false);
  saving = signal(false);
  showAddItem = signal(false);
  addingItem = signal(false);
  editingItemId = signal<string | null>(null);

  editName = '';
  editNumber = '';
  editAddr = '';
  editCity = '';
  editState = '';
  editZip = '';
  newSku = '';
  newItemName = '';
  newItemDesc = '';
  newPrice = '';
  newQty = '';
  editItemName = '';
  editItemDesc = '';
  editItemPrice = '';
  editItemQty = '';

  addressParts = computed(() => {
    const loc = this.location();
    if (!loc) return [];
    return [loc.address, loc.city, loc.state, loc.zipCode].filter(Boolean) as string[];
  });

  totalValue = computed(() => this.inventory().reduce((sum, i) => sum + i.price * i.quantityOnHand, 0));
  maxQty = computed(() => Math.max(...this.inventory().map((i) => i.quantityOnHand), 100));

  private get locationId(): string {
    return this.route.snapshot.paramMap.get('locationId')!;
  }

  ngOnInit() {
    this.loadData();
  }

  async loadData() {
    this.loading.set(true);
    this.error.set(null);
    try {
      const [loc, inv] = await Promise.all([
        this.api.get<LocationDetail>(`/api/locations/${this.locationId}`),
        this.api.get<PagedResponse<InventoryItemDto>>(`/api/locations/${this.locationId}/inventory?pageSize=50`),
      ]);
      this.location.set(loc);
      this.inventory.set(inv.data);
      this.editName = loc.name;
      this.editNumber = loc.storeNumber ?? '';
      this.editAddr = loc.address ?? '';
      this.editCity = loc.city ?? '';
      this.editState = loc.state ?? '';
      this.editZip = loc.zipCode ?? '';
    } catch (e: any) {
      this.error.set(e.message);
    } finally {
      this.loading.set(false);
    }
  }

  async handleSaveLocation() {
    const loc = this.location();
    if (!loc) return;
    this.saving.set(true); this.error.set(null);
    try {
      const updated = await this.api.put<LocationDetail>(`/api/locations/${loc.id}`, {
        name: this.editName.trim(),
        storeNumber: this.editNumber.trim() || null,
        address: this.editAddr.trim() || null,
        city: this.editCity.trim() || null,
        state: this.editState.trim() || null,
        zipCode: this.editZip.trim() || null,
      });
      this.location.set(updated);
      this.editing.set(false);
    } catch (e: any) {
      this.error.set(e.message);
    } finally {
      this.saving.set(false);
    }
  }

  async handleDeleteLocation() {
    const loc = this.location();
    if (!loc || !confirm(`Delete ${loc.name}? This cannot be undone.`)) return;
    try {
      await this.api.delete(`/api/locations/${loc.id}`);
      window.location.href = `/retail/chains/${loc.chainId}`;
    } catch (e: any) {
      this.error.set(e.message);
    }
  }

  async handleAddItem() {
    this.addingItem.set(true); this.error.set(null);
    try {
      await this.api.post(`/api/locations/${this.locationId}/inventory`, {
        sku: this.newSku.trim(),
        name: this.newItemName.trim(),
        description: this.newItemDesc.trim() || null,
        price: parseFloat(this.newPrice) || 0,
        quantityOnHand: parseInt(this.newQty) || 0,
      });
      this.newSku = ''; this.newItemName = ''; this.newItemDesc = '';
      this.newPrice = ''; this.newQty = '';
      this.showAddItem.set(false);
      await this.loadData();
    } catch (e: any) {
      this.error.set(e.message);
    } finally {
      this.addingItem.set(false);
    }
  }

  startEditItem(item: InventoryItemDto) {
    this.editingItemId.set(item.id);
    this.editItemName = item.name;
    this.editItemDesc = '';
    this.editItemPrice = item.price.toString();
    this.editItemQty = item.quantityOnHand.toString();
  }

  async handleRestock(item: InventoryItemDto) {
    try {
      await this.api.put(`/api/inventory/${item.id}`, {
        name: item.name,
        price: item.price,
        quantityOnHand: item.quantityOnHand + 50,
      });
      await this.loadData();
    } catch (e: any) {
      this.error.set(e.message);
    }
  }

  async handleSaveItem(itemId: string) {
    try {
      await this.api.put(`/api/inventory/${itemId}`, {
        name: this.editItemName.trim(),
        description: this.editItemDesc.trim() || null,
        price: parseFloat(this.editItemPrice) || 0,
        quantityOnHand: parseInt(this.editItemQty) || 0,
      });
      this.editingItemId.set(null);
      await this.loadData();
    } catch (e: any) {
      this.error.set(e.message);
    }
  }

  async handleDeleteItem(itemId: string, name: string) {
    if (!confirm(`Delete ${name}?`)) return;
    try {
      await this.api.delete(`/api/inventory/${itemId}`);
      await this.loadData();
    } catch (e: any) {
      this.error.set(e.message);
    }
  }
}
