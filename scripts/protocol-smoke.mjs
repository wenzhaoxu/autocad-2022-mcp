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

  const response = await client.callTool({ name: "cad_capabilities", arguments: {} });
  assert.equal(response.isError, undefined);
  assert.equal(response.structuredContent.version, VERSION);
  assert(response.structuredContent.tools.some((tool) => tool.name === "cad_model_build"));
  assert.equal(response.structuredContent.synchronization.changeJournal, true);
  process.stdout.write(`MCP protocol smoke passed: ${catalog.tools.length} tools\n`);
} finally {
  await client.close();
}
