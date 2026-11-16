import "./hosted.css";
import { renderHostedSideBar } from "@/embed/HostedSideBar";
import full from "../../public/brand/logo-full.png";
import icon from "../../public/brand/logo-icon.png";
import white from "../../public/brand/logo-white.png";
import { osduDeliveryModule } from "../module";
import { hostedDeliveryModule } from "./module";

// The OSDU Delivery extension's side bar: the working copy, and the menu of the module's pages and SQLFlow's.
renderHostedSideBar([osduDeliveryModule, hostedDeliveryModule], { logos: { full, icon, white } });
