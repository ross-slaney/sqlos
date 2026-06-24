import Image from "next/image";

type ProductScreenshotProps = {
  src: string;
  alt: string;
  priority?: boolean;
  className?: string;
};

export default function ProductScreenshot({
  src,
  alt,
  priority,
  className,
}: ProductScreenshotProps) {
  return (
    <div
      className={[
        "overflow-hidden rounded-lg border border-neon-cyan/25 bg-card shadow-[0_18px_70px_oklch(0_0_0_/_0.38),0_0_40px_oklch(0.82_0.17_200_/_0.08)] ring-1 ring-neon-green/10",
        className ?? "",
      ]
        .filter(Boolean)
        .join(" ")}
    >
      <div className="flex items-center gap-1.5 border-b border-border/70 bg-muted/50 px-4 py-2.5">
        <span className="h-2.5 w-2.5 rounded-full bg-neon-coral/80" />
        <span className="h-2.5 w-2.5 rounded-full bg-neon-yellow/80" />
        <span className="h-2.5 w-2.5 rounded-full bg-neon-green/80" />
        <span className="ml-2 text-[11px] font-medium text-muted-foreground">SqlOS surface</span>
      </div>
      <Image
        src={src}
        alt={alt}
        width={1280}
        height={800}
        priority={priority}
        className="h-auto w-full"
      />
    </div>
  );
}
