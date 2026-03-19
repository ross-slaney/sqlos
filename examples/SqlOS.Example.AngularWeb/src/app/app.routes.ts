import { Routes } from '@angular/router';
import { authGuard } from './guards/auth.guard';

export const routes: Routes = [
  {
    path: '',
    loadComponent: () => import('./pages/landing/landing.component').then(m => m.LandingComponent),
  },
  {
    path: 'login',
    loadComponent: () => import('./pages/login/login.component').then(m => m.LoginComponent),
  },
  {
    path: 'signup',
    loadComponent: () => import('./pages/signup/signup.component').then(m => m.SignupComponent),
  },
  {
    path: 'auth/authorize',
    loadComponent: () => import('./pages/auth-authorize/auth-authorize.component').then(m => m.AuthAuthorizeComponent),
  },
  {
    path: 'auth/callback',
    loadComponent: () => import('./pages/auth-callback/auth-callback.component').then(m => m.AuthCallbackComponent),
  },
  {
    path: 'retail',
    loadComponent: () => import('./pages/retail/retail-layout/retail-layout.component').then(m => m.RetailLayoutComponent),
    canActivate: [authGuard],
    children: [
      {
        path: '',
        loadComponent: () => import('./pages/retail/dashboard/dashboard.component').then(m => m.DashboardComponent),
      },
      {
        path: 'chains',
        loadComponent: () => import('./pages/retail/chains/chains.component').then(m => m.ChainsComponent),
      },
      {
        path: 'chains/:chainId',
        loadComponent: () => import('./pages/retail/chain-detail/chain-detail.component').then(m => m.ChainDetailComponent),
      },
      {
        path: 'stores',
        loadComponent: () => import('./pages/retail/stores/stores.component').then(m => m.StoresComponent),
      },
      {
        path: 'locations/:locationId',
        loadComponent: () => import('./pages/retail/location-detail/location-detail.component').then(m => m.LocationDetailComponent),
      },
    ],
  },
  {
    path: '**',
    redirectTo: '',
  },
];
