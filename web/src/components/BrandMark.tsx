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
          <stop stopColor="hsl(var(--theme-hue) 88% 72%)" />
          <stop offset="1" stopColor="hsl(var(--theme-hue) 74% 48%)" />
        </linearGradient>
      </defs>
      <rect width="64" height="64" rx="16" fill="url(#sqlos-header-gradient)" />
      <path
        d="M14 14C24 8 44 8 52 22C44 18 28 17 18 20C15 18 14 16 14 14Z"
        fill="#FFFFFF"
        fillOpacity="0.12"
      />
      <text
        x="50%"
        y="52%"
        fill="#FFFFFF"
        fontFamily="Manrope, 'Helvetica Neue', Arial, sans-serif"
        fontSize="26"
        fontWeight="800"
        letterSpacing="-1.25"
        textAnchor="middle"
        dominantBaseline="middle"
      >
        SO
      </text>
    </svg>
  );
}
