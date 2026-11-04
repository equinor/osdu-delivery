// The downloaded dashboard's script and styles, which vite.config.ts bundles from exportEntry.tsx on demand.
declare module "virtual:assertion-dashboard-export" {
  /** The dashboard as one script, React included, that renders over the page's #assertion-dashboard-data. */
  export const script: string;
  /** The dashboard's styles. */
  export const style: string;
}
