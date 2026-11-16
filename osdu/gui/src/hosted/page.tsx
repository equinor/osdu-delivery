import "./hosted.css";
import { renderHosted } from "@/embed/renderHosted";
import full from "../../public/brand/logo-full.png";
import icon from "../../public/brand/logo-icon.png";
import white from "../../public/brand/logo-white.png";
import { osduDeliveryModule } from "../module";
import { hostedDeliveryModule } from "./module";

// The pages the OSDU Delivery extension shows in the editor's tabs: SQLFlow's workbench pages and the module's, with the
// pages of a document being written.
renderHosted([osduDeliveryModule, hostedDeliveryModule], { logos: { full, icon, white } });
