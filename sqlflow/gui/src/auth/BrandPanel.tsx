import { branding } from "../modules/branding";
import { LoginStreams } from "./LoginStreams";
import { LOGIN_ACCENT } from "./loginStreamLanes";

// The product's brand on the pages a person signs in through, outside the workbench: the login page, and the page that
// approves a client's browser sign-in, which a person reaches straight after it. Both stand on the brand's navy field
// (BRAND_FIELD) with these, so signing in reads as one journey whichever page it ends on.

/** The product's lockup: the full logo and, when the product carries one, its "powered by" attribution beside it. */
export function Lockup({ logoClassName }: { logoClassName: string }) {
  const brand = branding();
  return (
    <div className="flex items-center gap-4">
      <img src={brand.logos.full} alt={brand.productName} className={`w-auto max-w-full object-contain ${logoClassName}`} />
      {brand.attribution !== null && (
        <>
          <span className="h-5 w-px shrink-0 bg-white/20" aria-hidden="true" />
          <span className="text-[0.8125rem] whitespace-nowrap text-white/55" data-testid="login-attribution">{brand.attribution}</span>
        </>
      )}
    </div>
  );
}

/**
 * The brand panel, shown only on wide screens: the lockup, a picture of the kinds of data the product takes flowing into
 * rings that echo the logo mark, and the product's pitch in two lines and one sentence. It is the first thing a person
 * sees of the product, so it says what the product is for and nothing more; the workbench explains the rest.
 */
export function BrandPanel() {
  const brand = branding();
  return (
    <section className="relative hidden flex-col text-white lg:flex lg:w-[54%] xl:w-[56%]" data-testid="login-brand-panel">
      {/* A fine dot grid, brightest around the rings and fading out toward the edges, so the field has a surface. */}
      <div
        className="pointer-events-none absolute inset-0"
        style={{
          backgroundImage: "radial-gradient(color-mix(in srgb, white 11%, transparent) 1px, transparent 1.5px)",
          backgroundSize: "24px 24px",
          maskImage: "radial-gradient(ellipse 75% 60% at 62% 42%, black 0%, transparent 72%)",
        }}
        aria-hidden="true"
      />
      {/* A soft light behind the pitch, so the gradient is not the only depth cue. */}
      <div
        className="pointer-events-none absolute -bottom-48 -left-40 size-[38rem] rounded-full"
        style={{ background: "radial-gradient(circle, color-mix(in srgb, var(--brand-blue) 22%, transparent) 0%, transparent 65%)" }}
        aria-hidden="true"
      />

      <header className="relative px-12 pt-11 xl:px-16 xl:pt-14">
        <Lockup logoClassName="h-9 xl:h-10" />
      </header>

      {/* The picture keeps between the lockup and the pitch; its light spills past them, fading out on its own. */}
      <div className="relative min-h-0 flex-1">
        <LoginStreams streams={brand.login.streams} />
      </div>

      <div className="relative px-12 pb-14 xl:px-16 xl:pb-16 motion-safe:animate-in motion-safe:fade-in-0 motion-safe:slide-in-from-bottom-2 motion-safe:duration-700">
        <h2 className="max-w-2xl text-[2.5rem] leading-[1.08] font-semibold tracking-[-0.025em] xl:text-[3.25rem]">
          <span className="block">{brand.login.headline}</span>
          <span className="block" style={{ color: LOGIN_ACCENT }}>{brand.login.headlineAccent}</span>
        </h2>
        <p className="mt-5 max-w-lg text-[0.9375rem] leading-relaxed text-white/60 xl:text-base">{brand.login.tagline}</p>
      </div>
    </section>
  );
}
