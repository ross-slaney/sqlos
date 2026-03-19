export interface PagedResponse<T> {
  data: T[];
  totalCount: number;
  hasMore: boolean;
  cursor?: string;
}

export interface ChainDto {
  id: string;
  resourceId: string;
  name: string;
  description?: string;
  locationCount: number;
  createdAt: string;
}

export interface ChainDetail {
  id: string;
  resourceId: string;
  name: string;
  description?: string;
  headquartersAddress?: string;
  locationCount: number;
  createdAt: string;
  updatedAt: string;
}

export interface LocationDto {
  id: string;
  resourceId: string;
  chainId: string;
  chainName?: string;
  name: string;
  storeNumber?: string;
  address?: string;
  city?: string;
  state?: string;
  zipCode?: string;
  createdAt: string;
}

export interface LocationDetail {
  id: string;
  resourceId: string;
  chainId: string;
  chainName?: string;
  name: string;
  storeNumber?: string;
  address?: string;
  city?: string;
  state?: string;
  zipCode?: string;
  inventoryItemCount: number;
  createdAt: string;
  updatedAt: string;
}

export interface InventoryItemDto {
  id: string;
  resourceId: string;
  locationId: string;
  sku: string;
  name: string;
  description?: string;
  price: number;
  quantityOnHand: number;
  createdAt: string;
}

export interface StoreSummary {
  id: string;
  chainId: string;
  chainName?: string;
  name: string;
  storeNumber?: string;
  city?: string;
  state?: string;
}

export interface DemoSubject {
  email: string | null;
  displayName: string;
  role?: string | null;
  description?: string | null;
  type: 'user' | 'agent' | 'service_account';
  credential: string | null;
}

export interface AuthOverride {
  type: 'agent' | 'service_account';
  header: string;
  value: string;
  displayName: string;
}

export interface StoreInventory {
  store: StoreSummary;
  items: InventoryItemDto[];
}

export interface HeadlessViewModel {
  requestId: string;
  view: string;
  clientId: string;
  headlessApiBasePath: string;
  error?: string | null;
  pendingToken?: string | null;
  email?: string | null;
  displayName?: string | null;
  uiContext?: Record<string, unknown> | null;
  providers?: HeadlessProvider[];
  organizationSelection?: HeadlessOrganizationOption[];
  settings?: HeadlessSettings | null;
  fieldErrors?: Record<string, string>;
}

export interface HeadlessProvider {
  connectionId: string;
  providerType: string;
  displayName: string;
}

export interface HeadlessOrganizationOption {
  id: string;
  name: string;
  primaryDomain?: string | null;
  role: string;
}

export interface HeadlessSettings {
  pageTitle?: string;
  pageSubtitle?: string;
  primaryColor?: string;
  accentColor?: string;
  backgroundColor?: string;
  enablePasswordSignup?: boolean;
}

export interface HeadlessActionResult {
  type: 'redirect' | 'view';
  redirectUrl?: string;
  viewModel?: HeadlessViewModel;
}

export interface DecodedToken {
  exp: number;
  iss?: string;
  sub?: string;
  email?: string;
  name?: string;
  org_id?: string;
  sid?: string;
}

export interface TokenResponse {
  access_token?: string;
  refresh_token?: string;
  error?: string;
  error_description?: string;
}

export interface SessionData {
  accessToken: string;
  refreshToken: string;
  userId: string;
  email: string;
  displayName: string;
  organizationId: string | null;
  sessionId: string;
  exp: number;
}
