import Link from "next/link";

export default function Footer() {
  return (
    <footer className="border-t border-stone-200 bg-stone-100">
      <div className="mx-auto max-w-6xl px-6 py-12">
        <div className="grid grid-cols-1 gap-8 md:grid-cols-4">
          <div className="md:col-span-2">
            <Link href="/" className="flex items-center gap-2">
              <div className="flex h-8 w-8 items-center justify-center rounded-lg bg-emerald-800 text-white font-bold text-sm">
                SO
              </div>
              <span className="text-xl font-bold text-stone-950">
                SqlOS
              </span>
            </Link>
            <p className="mt-4 max-w-md text-sm leading-6 text-stone-600">
              Open source embedded auth and authorization for .NET apps, with a
              shared example stack and docs-first integration story.
            </p>
          </div>
          <div>
            <h3 className="text-sm font-semibold text-stone-950">
              Resources
            </h3>
            <ul className="mt-4 space-y-2">
              <li>
                <Link
                  href="/docs"
                  className="text-sm text-stone-600 hover:text-stone-950"
                >
                  Documentation
                </Link>
              </li>
              <li>
                <Link
                  href="/docs#example-stack"
                  className="text-sm text-stone-600 hover:text-stone-950"
                >
                  Example stack
                </Link>
              </li>
              <li>
                <Link
                  href="/blog"
                  className="text-sm text-stone-600 hover:text-stone-950"
                >
                  Blog
                </Link>
              </li>
            </ul>
          </div>
          <div>
            <h3 className="text-sm font-semibold text-stone-950">
              Notes
            </h3>
            <ul className="mt-4 space-y-2">
              <li>
                <span className="text-sm text-stone-600">
                  AuthServer and Fga ship in one runtime.
                </span>
              </li>
              <li>
                <span className="text-sm text-stone-600">
                  The shared example is the main reference implementation.
                </span>
              </li>
            </ul>
          </div>
        </div>
        <div className="mt-12 border-t border-stone-200 pt-8">
          <p className="text-center text-sm text-stone-500">
            &copy; {new Date().getFullYear()} SqlOS. Open source, example-driven,
            and meant to be read alongside the docs and integration tests.
          </p>
        </div>
      </div>
    </footer>
  );
}
