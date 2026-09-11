import { CadBridgeClient } from "../dist/src/bridge.js";

const bridge = new CadBridgeClient();
const status = await bridge.call("status", {});
console.log(JSON.stringify(status, null, 2));

const inspection = await bridge.call("inspect", {
  scope: "drawing",
  includeExtents: true,
});
console.log(JSON.stringify(inspection, null, 2));
