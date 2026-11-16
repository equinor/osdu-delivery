import "./hosted.css";
import full from "../../public/brand/logo-full.png";
import icon from "../../public/brand/logo-icon.png";
import white from "../../public/brand/logo-white.png";
import { renderHosted } from "./renderHosted";

// SQLFlow's own hosted pages: every page of the workbench, one to a tab of the editor that shows it, with no GUI modules
// registered. A module build has an entry of its own that imports its stylesheet and passes its modules.
renderHosted([], { logos: { full, icon, white } });
