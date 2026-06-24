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
        "overflow-hidden rounded-2xl border bg-card shadow-lg ring-1 ring-border/60",
        className ?? "",
      ]
        .filter(Boolean)
        .join(" ")}
    >
      <div className="flex items-center gap-1.5 border-b bg-muted/50 px-4 py-2.5">
        <span className="h-2.5 w-2.5 rounded-full bg-red-400/80" />
        <span className="h-2.5 w-2.5 rounded-full bg-amber-400/80" />
        <span className="h-2.5 w-2.5 rounded-full bg-emerald-400/80" />
        <span className="ml-2 text-[11px] font-medium text-muted-foreground">SqlOS</span>
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
