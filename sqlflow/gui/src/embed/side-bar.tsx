import "./hosted.css";
import full from "../../public/brand/logo-full.png";
import icon from "../../public/brand/logo-icon.png";
import white from "../../public/brand/logo-white.png";
import { renderHostedSideBar } from "./HostedSideBar";

// SQLFlow's own side bar for an editor: the workbench's search and menu, with no GUI modules registered.
renderHostedSideBar([], { logos: { full, icon, white } });
