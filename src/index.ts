#!/usr/bin/env node
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { CadBridgeClient, CadBridgeError } from "./bridge.js";
import { buildModelRecipe } from "./recipe.js";
import { toolDefinitions } from "./tools.js";

const server = new McpServer({ name: "autocad-2024", version: "0.0.3" });
const bridge = new CadBridgeClient();

for (const tool of toolDefinitions) {
  server.registerTool(
    tool.name,
    {
      description: tool.description,
      inputSchema: tool.schema,
      annotations: {
        readOnlyHint: tool.readOnly ?? false,
        destructiveHint: tool.destructive ?? !(tool.readOnly ?? false),
        idempotentHint: tool.idempotent ?? (tool.readOnly ?? false),
        openWorldHint: false,
      },
    },
    async (args) => {
      try {
        const result = tool.method === "__local_instances"
          ? { instances: bridge.listInstances() }
          : tool.method === "__local_capabilities"
            ? {
                version: "0.0.3",
                autocad: "2024 / R24.3",
                tools: toolDefinitions.map(({ name, readOnly }) => ({ name, readOnly: readOnly ?? false })),
                limits: { drawEntities: 1000, solidPrimitives: 250, inspectEntities: 5000, measureHandles: 500 },
                coordinates: "Drawing units; draw uses current UCS by default, cad_solid uses WCS; angles are radians.",
                verifiedExports: ["dwg", "dxf", "stl"],
                queuedExports: ["pdf", "dwf", "svg", "png", "jpeg"],
              }
          : tool.method === "__local_connect"
            ? { connected: bridge.selectProcess((args as { processId: number }).processId) }
            : tool.method === "__local_model_build"
              ? await buildModelRecipe(bridge, args)
            : await bridge.call(tool.method, args);
        return {
          content: [{ type: "text" as const, text: JSON.stringify(result, null, 2) }],
          structuredContent: result && typeof result === "object" ? result as Record<string, unknown> : { value: result },
        };
      } catch (error) {
        const known = error instanceof CadBridgeError;
        return {
          isError: true,
          content: [{
            type: "text" as const,
            text: JSON.stringify({
              error: known ? error.code : "INTERNAL_ERROR",
              message: error instanceof Error ? error.message : String(error),
              details: known ? error.details : undefined,
            }, null, 2),
          }],
        };
      }
    },
  );
}

await server.connect(new StdioServerTransport());
