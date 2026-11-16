import { lazyRoute } from "@/lib/lazyRoute";
import type { GuiModule } from "@/modules/registry";

const WorkingCopyMappingPage = lazyRoute("WorkingCopyMappingPage", () => import("./WorkingCopyMappingPage"));

/** Where the page of a mapping being written is, for the editor that opens it. */
export const WORKING_COPY_MAPPING_PATH = "/delivery/working-copy/mapping";

/**
 * What the hosted build adds to the OSDU Delivery module: the pages that only make sense in an editor, because they read
 * a file the editor has open. The browser's build does not register it.
 */
export const hostedDeliveryModule: GuiModule = {
  id: "osdu-delivery-hosted",
  routes: [{ path: WORKING_COPY_MAPPING_PATH, component: WorkingCopyMappingPage }],
  detailTitles: [{ pattern: /^\/delivery\/working-copy\/mapping/, title: () => "Mapping" }],
};
