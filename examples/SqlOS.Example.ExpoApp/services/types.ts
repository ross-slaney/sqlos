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
  type: "user" | "agent" | "service_account";
  credential: string | null;
}

export interface StoreInventory {
  store: StoreSummary;
  items: InventoryItemDto[];
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

export interface DecodedToken {
  exp: number;
  iss?: string;
  sub?: string;
  email?: string;
  name?: string;
  org_id?: string;
  sid?: string;
}
