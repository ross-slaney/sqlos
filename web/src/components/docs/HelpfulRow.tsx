"use client";

import { useState } from "react";

export default function HelpfulRow() {
  const [answered, setAnswered] = useState(false);

  return (
    <div className="flex items-center gap-3 border-t pt-[22px] text-sm text-muted-foreground">
      {answered ? (
        <span>Thanks for the feedback.</span>
      ) : (
        <>
          Was this page helpful?
          <button
            type="button"
            onClick={() => setAnswered(true)}
            className="rounded-[4px] border bg-background px-[13px] py-[5px] text-[13px] font-medium text-foreground/80 transition-colors hover:border-primary hover:text-primary"
          >
            Yes
          </button>
          <button
            type="button"
            onClick={() => setAnswered(true)}
            className="rounded-[4px] border bg-background px-[13px] py-[5px] text-[13px] font-medium text-foreground/80 transition-colors hover:border-primary hover:text-primary"
          >
            No
          </button>
        </>
      )}
    </div>
  );
}
