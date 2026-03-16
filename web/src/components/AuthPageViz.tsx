"use client";

import { useEffect, useState } from "react";

const providerLogos: Record<string, React.ReactNode> = {
  Google: (
    <svg viewBox="0 0 24 24" className="h-4 w-4">
      <path d="M22.56 12.25c0-.78-.07-1.53-.2-2.25H12v4.26h5.92a5.06 5.06 0 01-2.2 3.32v2.77h3.57c2.08-1.92 3.28-4.74 3.28-8.1z" fill="#4285F4"/>
      <path d="M12 23c2.97 0 5.46-.98 7.28-2.66l-3.57-2.77c-.98.66-2.23 1.06-3.71 1.06-2.86 0-5.29-1.93-6.16-4.53H2.18v2.84C3.99 20.53 7.7 23 12 23z" fill="#34A853"/>
      <path d="M5.84 14.09c-.22-.66-.35-1.36-.35-2.09s.13-1.43.35-2.09V7.07H2.18C1.43 8.55 1 10.22 1 12s.43 3.45 1.18 4.93l2.85-2.22.81-.62z" fill="#FBBC05"/>
      <path d="M12 5.38c1.62 0 3.06.56 4.21 1.64l3.15-3.15C17.45 2.09 14.97 1 12 1 7.7 1 3.99 3.47 2.18 7.07l3.66 2.84c.87-2.6 3.3-4.53 6.16-4.53z" fill="#EA4335"/>
    </svg>
  ),
  Microsoft: (
    <svg viewBox="0 0 24 24" className="h-3.5 w-3.5">
      <rect x="1" y="1" width="10" height="10" fill="#F25022"/>
      <rect x="13" y="1" width="10" height="10" fill="#7FBA00"/>
      <rect x="1" y="13" width="10" height="10" fill="#00A4EF"/>
      <rect x="13" y="13" width="10" height="10" fill="#FFB900"/>
    </svg>
  ),
  Apple: (
    <svg viewBox="0 0 24 24" className="h-4 w-4" fill="#1c1917">
      <path d="M18.71 19.5c-.83 1.24-1.71 2.45-3.05 2.47-1.34.03-1.77-.79-3.29-.79-1.53 0-2 .77-3.27.82-1.31.05-2.3-1.32-3.14-2.53C4.25 17 2.94 12.45 4.7 9.39c.87-1.52 2.43-2.48 4.12-2.51 1.28-.02 2.5.87 3.29.87.78 0 2.26-1.07 3.8-.91.65.03 2.47.26 3.64 1.98-.09.06-2.17 1.28-2.15 3.81.03 3.02 2.65 4.03 2.68 4.04-.03.07-.42 1.44-1.38 2.83M13 3.5c.73-.83 1.94-1.46 2.94-1.5.13 1.17-.34 2.35-1.04 3.19-.69.85-1.83 1.51-2.95 1.42-.15-1.15.41-2.35 1.05-3.11z"/>
    </svg>
  ),
};

const providers = [
  { name: "Google" },
  { name: "Microsoft" },
  { name: "Apple" },
];

export default function AuthPageViz() {
  const [stage, setStage] = useState(0);
  const [typing, setTyping] = useState("");
  const email = "sarah@acme.co";

  useEffect(() => {
    const timers: ReturnType<typeof setTimeout>[] = [];

    // Stage 0: page appears (immediate)
    // Stage 1: start typing email
    timers.push(setTimeout(() => setStage(1), 800));

    // Type email character by character
    for (let i = 0; i <= email.length; i++) {
      timers.push(
        setTimeout(() => setTyping(email.slice(0, i)), 1200 + i * 60)
      );
    }

    // Stage 2: email submitted, show SSO discovery
    timers.push(setTimeout(() => setStage(2), 1200 + email.length * 60 + 500));

    // Stage 3: redirect to SAML
    timers.push(setTimeout(() => setStage(3), 1200 + email.length * 60 + 1800));

    // Stage 4: authenticated
    timers.push(setTimeout(() => setStage(4), 1200 + email.length * 60 + 3000));

    // Loop
    timers.push(
      setTimeout(() => {
        setStage(0);
        setTyping("");
      }, 1200 + email.length * 60 + 5500)
    );

    const loop = setInterval(() => {
      setStage(0);
      setTyping("");

      for (let i = 0; i <= email.length; i++) {
        timers.push(
          setTimeout(() => setTyping(email.slice(0, i)), 1200 + i * 60)
        );
      }
      timers.push(setTimeout(() => setStage(1), 800));
      timers.push(
        setTimeout(() => setStage(2), 1200 + email.length * 60 + 500)
      );
      timers.push(
        setTimeout(() => setStage(3), 1200 + email.length * 60 + 1800)
      );
      timers.push(
        setTimeout(() => setStage(4), 1200 + email.length * 60 + 3000)
      );
    }, 1200 + email.length * 60 + 6000);

    return () => {
      timers.forEach(clearTimeout);
      clearInterval(loop);
    };
  }, []);

  return (
    <div className="relative">
      {/* Browser chrome */}
      <div className="overflow-hidden rounded-xl border border-stone-200 bg-white shadow-[0_8px_40px_rgba(0,0,0,0.06)]">
        {/* URL bar */}
        <div className="flex items-center gap-2 border-b border-stone-100 bg-stone-50 px-4 py-2.5">
          <div className="flex gap-1.5">
            <span className="h-2.5 w-2.5 rounded-full bg-stone-200" />
            <span className="h-2.5 w-2.5 rounded-full bg-stone-200" />
            <span className="h-2.5 w-2.5 rounded-full bg-stone-200" />
          </div>
          <div className="ml-2 flex-1 rounded-md bg-white px-3 py-1 text-[11px] text-stone-400 border border-stone-150">
            app.yourproduct.com/sqlos/auth/login
          </div>
        </div>

        {/* Auth page content */}
        <div className="flex min-h-[340px] items-center justify-center bg-gradient-to-b from-stone-50 to-white p-8">
          <div
            className="w-full max-w-[280px] transition-all duration-500"
            style={{ opacity: stage >= 0 ? 1 : 0, transform: stage >= 0 ? "translateY(0)" : "translateY(8px)" }}
          >
            {/* Logo area */}
            <div className="mb-6 text-center">
              <div className="mx-auto mb-2 flex h-9 w-9 items-center justify-center rounded-lg bg-stone-900 text-[11px] font-bold text-white">
                YP
              </div>
              <div className="text-[15px] font-semibold text-stone-900">
                Sign in to YourProduct
              </div>
            </div>

            {stage < 4 ? (
              <>
                {/* Email input */}
                <div className="mb-3">
                  <div
                    className="flex items-center rounded-lg border bg-white px-3 py-2.5 text-[13px] transition-colors duration-200"
                    style={{
                      borderColor: stage >= 1 ? "#a8a29e" : "#e7e5e4",
                    }}
                  >
                    <span className={typing ? "text-stone-900" : "text-stone-400"}>
                      {typing || "name@company.com"}
                    </span>
                    {stage >= 1 && stage < 2 && (
                      <span className="ml-0.5 inline-block h-4 w-[1.5px] animate-pulse bg-stone-900" />
                    )}
                  </div>
                </div>

                {/* Continue button */}
                <button
                  className="mb-4 w-full rounded-lg py-2.5 text-[13px] font-semibold text-white transition-colors duration-300"
                  style={{
                    backgroundColor: stage >= 2 ? "#16a34a" : "#1c1917",
                  }}
                >
                  {stage >= 2 ? "Continue with SSO" : "Continue"}
                </button>

                {/* SSO discovery message */}
                <div
                  className="overflow-hidden transition-all duration-400"
                  style={{
                    maxHeight: stage >= 2 ? "60px" : "0",
                    opacity: stage >= 2 ? 1 : 0,
                  }}
                >
                  <div className="mb-4 rounded-lg bg-emerald-50 border border-emerald-200 px-3 py-2 text-center text-[12px] text-emerald-700">
                    {stage >= 3 ? (
                      <span className="flex items-center justify-center gap-1.5">
                        <span className="inline-block h-3 w-3 animate-spin rounded-full border-2 border-emerald-300 border-t-emerald-600" />
                        Redirecting to Okta...
                      </span>
                    ) : (
                      "acme.co uses SSO — redirecting"
                    )}
                  </div>
                </div>

                {/* Divider */}
                {stage < 2 && (
                  <div className="mb-4 flex items-center gap-3">
                    <span className="h-px flex-1 bg-stone-200" />
                    <span className="text-[11px] text-stone-400">or</span>
                    <span className="h-px flex-1 bg-stone-200" />
                  </div>
                )}

                {/* Social providers */}
                {stage < 2 && (
                  <div className="space-y-2">
                    {providers.map((p) => (
                      <button
                        key={p.name}
                        className="flex w-full items-center justify-center gap-2 rounded-lg border border-stone-200 bg-white py-2 text-[13px] font-medium text-stone-700 transition-colors hover:bg-stone-50"
                      >
                        {providerLogos[p.name]}
                        Continue with {p.name}
                      </button>
                    ))}
                  </div>
                )}
              </>
            ) : (
              /* Authenticated state */
              <div className="text-center transition-all duration-500" style={{ opacity: stage >= 4 ? 1 : 0 }}>
                <div className="mx-auto mb-3 flex h-10 w-10 items-center justify-center rounded-full bg-emerald-100">
                  <svg className="h-5 w-5 text-emerald-600" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2.5}>
                    <path strokeLinecap="round" strokeLinejoin="round" d="M5 13l4 4L19 7" />
                  </svg>
                </div>
                <div className="text-[14px] font-semibold text-stone-900">
                  Authenticated
                </div>
                <div className="mt-1 text-[12px] text-stone-500">
                  Redirecting to YourProduct...
                </div>
              </div>
            )}
          </div>
        </div>
      </div>

      {/* Auth server annotation */}
      <div
        className="absolute -right-3 top-16 transition-all duration-500 sm:-right-4"
        style={{
          opacity: stage >= 2 ? 1 : 0,
          transform: stage >= 2 ? "translateX(0)" : "translateX(-8px)",
        }}
      >
        <div className="rounded-lg border border-emerald-200 bg-emerald-50 px-3 py-2 shadow-sm">
          <div className="text-[10px] font-semibold tracking-[0.08em] uppercase text-emerald-600">
            AuthServer
          </div>
          <div className="mt-0.5 text-[11px] text-emerald-700">
            Home realm discovery
          </div>
        </div>
      </div>
    </div>
  );
}
