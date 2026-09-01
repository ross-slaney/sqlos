import { ApplicationConfig, inject, provideAppInitializer, provideZoneChangeDetection } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { provideRouter, withComponentInputBinding } from '@angular/router';
import { OAuthService, provideOAuthClient } from 'angular-oauth2-oidc';
import { routes } from './app.routes';
import { createSqlOSAuthConfig } from './auth.config';

export const appConfig: ApplicationConfig = {
  providers: [
    provideZoneChangeDetection({ eventCoalescing: true }),
    provideRouter(routes, withComponentInputBinding()),
    provideHttpClient(),
    provideOAuthClient(),
    provideAppInitializer(() => {
      const oauth = inject(OAuthService);
      oauth.configure(createSqlOSAuthConfig());
      return oauth.loadDiscoveryDocumentAndTryLogin();
    }),
  ],
};
