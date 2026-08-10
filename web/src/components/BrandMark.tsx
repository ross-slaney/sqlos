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
          id="sqlos-brand-gradient"
          x1="8"
          y1="4"
          x2="56"
          y2="60"
          gradientUnits="userSpaceOnUse"
        >
          <stop stopColor="#4f46e5" />
          <stop offset="1" stopColor="#7c69f5" />
        </linearGradient>
      </defs>
      <rect width="64" height="64" rx="17" fill="url(#sqlos-brand-gradient)" />
      <g stroke="#FFFFFF" strokeWidth="4.4" strokeLinecap="round">
        <ellipse cx="32" cy="18" rx="16.5" ry="7" />
        <path d="M15.5 18v13.5c0 3.9 7.4 7 16.5 7s16.5-3.1 16.5-7V18" />
        <path d="M15.5 31.5V45c0 3.9 7.4 7 16.5 7s16.5-3.1 16.5-7V31.5" />
      </g>
    </svg>
  );
}
