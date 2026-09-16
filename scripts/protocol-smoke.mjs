import assert from "node:assert/strict";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";
import { VERSION } from "../dist/src/version.js";

const client = new Client({ name: "autocad-2024-mcp-smoke", version: "1.0.0" });
const transport = new StdioClientTransport({
  command: process.execPath,
  args: ["dist/src/index.js"],
  stderr: "pipe",
});

try {
  await client.connect(transport);
  const catalog = await client.listTools();
  const names = catalog.tools.map((tool) => tool.name);
  assert(names.includes("cad_model_build"), "cad_model_build is missing from the MCP catalog");
  assert(names.includes("cad_export"), "cad_export is missing from the MCP catalog");
  assert(names.includes("cad_text_style"), "cad_text_style is missing from the MCP catalog");
  assert(names.includes("cad_events"), "cad_events is missing from the MCP catalog");
  assert(names.includes("cad_electrical_symbol"), "cad_electrical_symbol is missing from the MCP catalog");
  assert(names.includes("cad_electrical_sheet"), "cad_electrical_sheet is missing from the MCP catalog");

  const resources = await client.listResources();
  const resourceUris = resources.resources.map((resource) => resource.uri);
  assert(resourceUris.includes("cad://standards/electrical-drawing-sheet"));
  assert(resourceUris.includes("cad://symbols/electrical/catalog"));
  assert(resourceUris.includes("cad://symbols/electrical/motor-3phase"));
  const sheetStandard = await client.readResource({ uri: "cad://standards/electrical-drawing-sheet" });
  assert(sheetStandard.contents.some((content) => content.text?.includes("A0")));
  const symbol = await client.readResource({ uri: "cad://symbols/electrical/motor-3phase" });
  assert(symbol.contents.some((content) => content.text?.includes("三相电动机")));

  const response = await client.callTool({ name: "cad_capabilities", arguments: {} });
  assert.equal(response.isError, undefined);
  assert.equal(response.structuredContent.version, VERSION);
  assert(response.structuredContent.tools.some((tool) => tool.name === "cad_model_build"));
  assert.equal(response.structuredContent.synchronization.changeJournal, true);
  process.stdout.write(`MCP protocol smoke passed: ${catalog.tools.length} tools\n`);
} finally {
  await client.close();
}
