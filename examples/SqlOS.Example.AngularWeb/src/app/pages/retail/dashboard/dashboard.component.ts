import { Component, inject, OnInit, signal, computed } from '@angular/core';
import { RouterLink } from '@angular/router';
import { AuthService } from '../../../services/auth.service';
import { ApiService } from '../../../services/api.service';
import { NorthyAssistantComponent } from '../../../components/northy-assistant/northy-assistant.component';
import {
  PagedResponse, StoreSummary, ChainDto, InventoryItemDto,
  StoreInventory, DemoSubject,
} from '../../../models';
import { environment } from '../../../environments/environment';

function inferRole(userName: string, demoUsers: DemoSubject[], email?: string | null): { roleName: string; roleLevel: 'admin' | 'chain' | 'store' | 'clerk' | 'none' } {
  const matched = demoUsers.find((u) => u.email === email);
  const role = matched?.role ?? '';
  if (/CompanyAdmin/i.test(role) || /org_admin/i.test(role)) return { roleName: 'Company Admin', roleLevel: 'admin' };
  if (/ChainManager/i.test(role)) return { roleName: 'Chain Manager', roleLevel: 'chain' };
  if (/StoreManager/i.test(role)) return { roleName: 'Store Manager', roleLevel: 'store' };
  if (/StoreClerk/i.test(role)) return { roleName: 'Store Clerk', roleLevel: 'clerk' };
  if (matched && !role) return { roleName: 'Member', roleLevel: 'none' };
  if (/admin/i.test(userName)) return { roleName: 'Admin', roleLevel: 'admin' };
  if (/manager/i.test(userName)) return { roleName: 'Manager', roleLevel: 'store' };
  if (/clerk/i.test(userName)) return { roleName: 'Clerk', roleLevel: 'clerk' };
  if (/regional/i.test(userName)) return { roleName: 'Regional', roleLevel: 'chain' };
  return { roleName: 'Team Member', roleLevel: 'none' };
}

function getGreeting(): string {
  const h = new Date().getHours();
  if (h < 12) return 'Good morning';
  if (h < 17) return 'Good afternoon';
  return 'Good evening';
}

@Component({
  selector: 'app-dashboard',
  standalone: true,
  imports: [RouterLink, NorthyAssistantComponent],
  template: `
    <div class="gap-20">
      <div class="dash-greeting">
        <div class="dash-greeting-text">
          <div class="dash-greeting-top">
            <h1>{{ greeting }}, {{ displayFirstName() }}</h1>
            <span class="badge badge-primary">{{ roleInfo().roleName }}</span>
          </div>
          <p class="muted">Here's what's happening across your retail operation.</p>
        </div>
      </div>

      <app-northy-assistant [message]="northyMessage()" [mood]="northyMood()" />

      @if (error()) {
        <p class="error">{{ error() }}</p>
      }

      <div class="stats-grid">
        <a routerLink="/retail/chains" class="stat-card stat-card--chains">
          <div class="stat-card-label">Chains</div>
          <div class="stat-card-value">{{ loading() ? '—' : chains().length }}</div>
          <div class="stat-card-sub">{{ loading() ? 'Loading...' : chains().length + ' visible to you' }}</div>
        </a>
        <a routerLink="/retail/stores" class="stat-card stat-card--stores">
          <div class="stat-card-label">Stores</div>
          <div class="stat-card-value">{{ loading() ? '—' : stores().length }}</div>
          <div class="stat-card-sub">{{ loading() ? 'Loading...' : 'Across ' + groupedChains().length + ' chain' + (groupedChains().length !== 1 ? 's' : '') }}</div>
        </a>
        <div class="stat-card stat-card--items">
          <div class="stat-card-label">Inventory</div>
          <div class="stat-card-value">{{ loading() ? '—' : allItems().length }}</div>
          <div class="stat-card-sub">
            @if (loading()) {
              Loading...
            } @else if (lowStockItems().length > 0) {
              <span class="warn">{{ lowStockItems().length }} low</span> · \${{ totalValue().toLocaleString(undefined, { maximumFractionDigits: 0 }) }} value
            } @else {
              \${{ totalValue().toLocaleString(undefined, { maximumFractionDigits: 0 }) }} total value
            }
          </div>
        </div>
      </div>

      @if (!loading() && quickActions().length > 0) {
        <div class="quick-actions">
          @for (a of quickActions(); track a.label) {
            <a [routerLink]="a.href" class="quick-action">
              <span class="quick-action-icon">{{ a.icon }}</span>
              <span>{{ a.label }}</span>
            </a>
          }
        </div>
      }

      @if (!loading() && lowStockItems().length > 0) {
        <div class="card alert-card">
          <div class="alert-card-header">
            <span class="alert-card-icon">⚠️</span>
            <div>
              <h3>Low Stock Alert</h3>
              <p class="muted">{{ lowStockItems().length }} item{{ lowStockItems().length !== 1 ? 's need' : ' needs' }} attention</p>
            </div>
          </div>
          <div class="alert-items">
            @for (item of lowStockItems(); track item.id) {
              <a [routerLink]="'/retail/locations/' + item.storeId" class="alert-item">
                <div class="alert-item-info">
                  <strong>{{ item.name }}</strong>
                  <span class="muted">{{ item.storeName }} · {{ item.sku }}</span>
                </div>
                <div class="alert-item-right">
                  <span class="stock-qty" [class.out]="item.quantityOnHand === 0" [class.low]="item.quantityOnHand > 0">
                    {{ item.quantityOnHand }} left
                  </span>
                </div>
              </a>
            }
          </div>
        </div>
      }

      @if (!loading() && storeInventories().length > 0 && hasInventoryData()) {
        <div class="card">
          <h2>Inventory by Store</h2>
          <div class="store-inv-grid">
            @for (si of storeInventories(); track si.store.id) {
              @if (si.items.length > 0) {
                <a [routerLink]="'/retail/locations/' + si.store.id" class="store-inv-card">
                  <div class="store-inv-card-header">
                    <strong>{{ si.store.name }}</strong>
                    @if (storeLowCount(si) > 0) {
                      <span class="badge badge-warning">{{ storeLowCount(si) }} low</span>
                    }
                  </div>
                  <div class="store-inv-stats">
                    <div><span class="store-inv-num">{{ si.items.length }}</span><span class="store-inv-label">items</span></div>
                    <div><span class="store-inv-num">\${{ (storeValue(si) / 1000).toFixed(1) }}k</span><span class="store-inv-label">value</span></div>
                  </div>
                  <div class="store-inv-bar">
                    @for (item of si.items; track item.id) {
                      <div class="store-inv-bar-seg"
                           [class.ok]="item.quantityOnHand > 10"
                           [class.low]="item.quantityOnHand > 0 && item.quantityOnHand <= 10"
                           [class.out]="item.quantityOnHand === 0"
                           [style.flex]="item.price * item.quantityOnHand"
                           [title]="item.name + ': ' + item.quantityOnHand + ' units'">
                      </div>
                    }
                  </div>
                </a>
              }
            }
          </div>
        </div>
      }

      @if (!loading() && groupedChains().length === 0) {
        <div class="empty-state">
          <strong>No data visible</strong>
          <p>Your current identity has no authorization grants. Switch to a different user in the sidebar to see data filtered by FGA.</p>
        </div>
      } @else if (!loading()) {
        <div class="card">
          <h2>Your Stores</h2>
          @for (group of groupedChains(); track group.chainId) {
            <div class="chain-group">
              <div class="chain-group-header">
                <a [routerLink]="'/retail/chains/' + group.chainId">{{ group.chainName }}</a>
                <span class="badge badge-neutral">{{ group.stores.length }}</span>
              </div>
              <table class="data-table">
                <thead><tr><th>Store</th><th>Store #</th><th>City</th><th>State</th></tr></thead>
                <tbody>
                  @for (store of group.stores; track store.id) {
                    <tr>
                      <td><a [routerLink]="'/retail/locations/' + store.id">{{ store.name }}</a></td>
                      <td class="mono">{{ store.storeNumber ?? '—' }}</td>
                      <td>{{ store.city ?? '—' }}</td>
                      <td>{{ store.state ?? '—' }}</td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
          }
        </div>
      }
    </div>
  `,
})
export class DashboardComponent implements OnInit {
  private auth = inject(AuthService);
  private api = inject(ApiService);

  greeting = getGreeting();

  stores = signal<StoreSummary[]>([]);
  chains = signal<ChainDto[]>([]);
  storeInventories = signal<StoreInventory[]>([]);
  loading = signal(true);
  error = signal<string | null>(null);
  demoUsers = signal<DemoSubject[]>([]);

  userName = computed(() => this.auth.user()?.name ?? this.auth.user()?.email ?? 'User');
  displayFirstName = computed(() => {
    const name = this.userName();
    const first = name.split(' ')[0];
    return first.length > 2 ? first : name;
  });

  roleInfo = computed(() => inferRole(this.userName(), this.demoUsers(), this.auth.user()?.email));

  allItems = computed(() => this.storeInventories().flatMap((si) => si.items));
  lowStockItems = computed(() =>
    this.storeInventories().flatMap((si) =>
      si.items.filter((i) => i.quantityOnHand <= 10).map((i) => ({ ...i, storeName: si.store.name, storeId: si.store.id }))
    )
  );
  totalValue = computed(() => this.allItems().reduce((s, i) => s + i.price * i.quantityOnHand, 0));
  hasInventoryData = computed(() => this.storeInventories().some((si) => si.items.length > 0));

  groupedChains = computed(() => {
    const storesByChain = new Map<string, { chainId: string; chainName: string; stores: StoreSummary[] }>();
    for (const store of this.stores()) {
      const existing = storesByChain.get(store.chainId);
      if (existing) { existing.stores.push(store); continue; }
      storesByChain.set(store.chainId, { chainId: store.chainId, chainName: store.chainName ?? store.chainId, stores: [store] });
    }
    return Array.from(storesByChain.values())
      .map((g) => ({ ...g, stores: [...g.stores].sort((a, b) => a.name.localeCompare(b.name)) }))
      .sort((a, b) => a.chainName.localeCompare(b.chainName));
  });

  northyMessage = computed(() => {
    if (this.loading()) return "Hang on, I'm loading your data...";
    if (this.stores().length === 0) return "I can't see any data right now — that's FGA in action! Try switching to a different identity in the sidebar.";
    if (this.lowStockItems().length > 0) return `Heads up! ${this.lowStockItems().length} item${this.lowStockItems().length > 1 ? 's are' : ' is'} running low on stock. You might want to restock before they sell out.`;
    const rl = this.roleInfo().roleLevel;
    if (rl === 'admin') return `Everything looks great across your ${this.chains().length} chain${this.chains().length !== 1 ? 's' : ''} and ${this.stores().length} store${this.stores().length !== 1 ? 's' : ''}. All ${this.allItems().length} items are well-stocked!`;
    if (rl === 'chain') return `Your ${this.stores().length} store${this.stores().length !== 1 ? 's are' : ' is'} looking good. ${this.allItems().length} item${this.allItems().length !== 1 ? 's' : ''} all stocked and ready to go!`;
    if (rl === 'store' || rl === 'clerk') return `Your store has ${this.allItems().length} item${this.allItems().length !== 1 ? 's' : ''} tracked. Everything is in stock — nice work keeping the shelves full!`;
    return `You've got ${this.stores().length} store${this.stores().length !== 1 ? 's' : ''} with ${this.allItems().length} item${this.allItems().length !== 1 ? 's' : ''} visible. Looking good!`;
  });

  northyMood = computed<'happy' | 'alert' | 'wave' | 'thinking'>(() => {
    if (this.loading()) return 'thinking';
    if (this.stores().length === 0) return 'wave';
    if (this.lowStockItems().length > 0) return 'alert';
    return 'happy';
  });

  quickActions = computed(() => {
    const actions: { label: string; href: string; icon: string }[] = [];
    const rl = this.roleInfo().roleLevel;
    if (rl === 'admin') {
      actions.push({ label: 'Add Chain', href: '/retail/chains', icon: '🏢' });
      actions.push({ label: 'Add Store', href: '/retail/stores', icon: '📍' });
    }
    if (rl === 'admin' || rl === 'chain') {
      actions.push({ label: 'View All Stores', href: '/retail/stores', icon: '🗺️' });
    }
    if (this.stores().length === 1) {
      actions.push({ label: 'View Inventory', href: `/retail/locations/${this.stores()[0].id}`, icon: '📦' });
    }
    if (this.stores().length > 0) {
      actions.push({ label: 'Browse Chains', href: '/retail/chains', icon: '🔍' });
    }
    return actions;
  });

  storeValue(si: StoreInventory): number {
    return si.items.reduce((s, i) => s + i.price * i.quantityOnHand, 0);
  }

  storeLowCount(si: StoreInventory): number {
    return si.items.filter((i) => i.quantityOnHand <= 10).length;
  }

  async ngOnInit() {
    fetch(`${environment.apiUrl}/api/demo/users`)
      .then((r) => r.json())
      .then((data: DemoSubject[]) => this.demoUsers.set(data))
      .catch(() => {});

    try {
      this.loading.set(true);
      const [locRes, chainRes] = await Promise.all([
        this.api.get<PagedResponse<StoreSummary>>('/api/locations?pageSize=250'),
        this.api.get<PagedResponse<ChainDto>>('/api/chains?pageSize=50'),
      ]);
      this.stores.set(locRes.data);
      this.chains.set(chainRes.data);

      const invResults = await Promise.all(
        locRes.data.map(async (store) => {
          const inv = await this.api.get<PagedResponse<InventoryItemDto>>(
            `/api/locations/${store.id}/inventory?pageSize=250`
          ).catch(() => ({ data: [] as InventoryItemDto[], totalCount: 0, hasMore: false }));
          return { store, items: inv.data };
        })
      );
      this.storeInventories.set(invResults);
    } catch (e: any) {
      this.error.set(e.message);
    } finally {
      this.loading.set(false);
    }
  }
}
