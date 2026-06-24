export default function BrandMark({ className }: { className?: string }) {
  return (
    <svg
      aria-hidden="true"
      viewBox="0 0 64 64"
      className={className}
      fill="none"
      xmlns="http://www.w3.org/2000/svg"
    >
      <defs>
        <linearGradient
          id="sqlos-header-gradient"
          x1="10"
          y1="6"
          x2="54"
          y2="58"
          gradientUnits="userSpaceOnUse"
        >
          <stop stopColor="oklch(0.88 0.2 146)" />
          <stop offset="0.54" stopColor="oklch(0.82 0.17 200)" />
          <stop offset="1" stopColor="oklch(0.72 0.2 24)" />
        </linearGradient>
        <filter id="sqlos-header-glow" x="-40%" y="-40%" width="180%" height="180%">
          <feGaussianBlur stdDeviation="2.5" result="blur" />
          <feMerge>
            <feMergeNode in="blur" />
            <feMergeNode in="SourceGraphic" />
          </feMerge>
        </filter>
      </defs>
      <rect width="64" height="64" rx="10" fill="oklch(0.08 0.026 248)" />
      <rect
        x="3"
        y="3"
        width="58"
        height="58"
        rx="8"
        stroke="url(#sqlos-header-gradient)"
        strokeWidth="2"
        opacity="0.9"
      />
      <path
        d="M20 15H44C46.7614 15 49 17.2386 49 20V44C49 46.7614 46.7614 49 44 49H20C17.2386 49 15 46.7614 15 44V20C15 17.2386 17.2386 15 20 15Z"
        fill="oklch(0.16 0.04 238 / 0.86)"
        stroke="oklch(0.82 0.17 200)"
        strokeWidth="2"
        filter="url(#sqlos-header-glow)"
      />
      <path d="M23 25H41M23 32H37M23 39H42" stroke="oklch(0.88 0.2 146)" strokeWidth="3" strokeLinecap="round" />
      <path d="M18 18L46 46" stroke="oklch(0.72 0.2 24 / 0.55)" strokeWidth="1.5" />
    </svg>
  );
}
