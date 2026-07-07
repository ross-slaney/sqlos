type SectionHeadingProps = {
  index: string;
  eyebrow: string;
  title: string;
  description?: string;
  dark?: boolean;
  align?: "left" | "center";
};

export default function SectionHeading({
  index,
  eyebrow,
  title,
  description,
  dark,
  align = "left",
}: SectionHeadingProps) {
  const muted = dark ? "text-zinc-400" : "text-muted-foreground";
  const strong = dark ? "text-zinc-50" : "text-foreground";
  const rule = dark ? "border-white/10" : "border-border";
  const centered = align === "center";

  return (
    <div className={centered ? "mx-auto max-w-2xl text-center" : "max-w-3xl"}>
      <div
        className={[
          "flex items-center gap-3 font-mono text-[11px] uppercase tracking-[0.22em]",
          centered ? "justify-center" : "",
        ].join(" ")}
      >
        <span className="text-primary">[{index}]</span>
        <span className={muted}>{eyebrow}</span>
        {!centered && <span className={`h-px flex-1 border-t border-dashed ${rule}`} />}
      </div>
      <h2
        className={`mt-5 text-balance text-3xl font-semibold leading-[1.05] tracking-[-0.045em] sm:text-[2.75rem] ${strong}`}
      >
        {title}
      </h2>
      {description ? (
        <p className={`mt-5 text-base leading-7 ${muted}`}>{description}</p>
      ) : null}
    </div>
  );
}
