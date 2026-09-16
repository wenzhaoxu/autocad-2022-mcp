#!/usr/bin/env node
import { randomUUID } from "node:crypto";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { AuditLogger, type AuditRecord } from "./audit.js";
import { CadBridgeClient, CadBridgeError } from "./bridge.js";
import { SERVER_INSTRUCTIONS } from "./instructions.js";
import { buildModelRecipe } from "./recipe.js";
import { VENTILATION_CONTROL_STANDARD } from "./resources.js";
import { toolDefinitions } from "./tools.js";
import { VERSION } from "./version.js";
import { CadWorkflowError, CadWorkflowExecutor } from "./workflow.js";

const server = new McpServer(
  { name: "autocad-2024", version: VERSION },
  { instructions: SERVER_INSTRUCTIONS },
);
const bridge = new CadBridgeClient();
const workflow = new CadWorkflowExecutor(bridge, toolDefinitions);
const audit = new AuditLogger();

async function safeAudit(record: AuditRecord): Promise<void> {
  try {
    await audit.write(record);
  } catch (error) {
    console.error(
      `[autocad-mcp] audit write failed: ${error instanceof Error ? error.message : String(error)}`,
    );
  }
}

server.registerResource(
  "ventilation-duty-standby",
  "cad://standards/ventilation-duty-standby",
  {
    title: "Duty/standby ventilation control baseline",
    description: "Engineering baseline for ordinary two-fan duty/standby ventilation control.",
    mimeType: "text/markdown",
  },
  async (uri) => ({
    contents: [
      {
        uri: uri.href,
        mimeType: "text/markdown",
        text: VENTILATION_CONTROL_STANDARD,
      },
    ],
  }),
);

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
    async (args, extra) => {
      const requestId = randomUUID();
      const startedAt = Date.now();
      await safeAudit({
        timestamp: new Date().toISOString(),
        requestId,
        tool: tool.name,
        status: "started",
        arguments: args,
      });

      try {
        const result =
          tool.method === "__local_instances"
            ? { instances: bridge.listInstances() }
            : tool.method === "__local_capabilities"
              ? {
                  version: VERSION,
                  autocad: "2024 / R24.3",
                  tools: toolDefinitions.map(({ name, readOnly }) => ({
                    name,
                    readOnly: readOnly ?? false,
                  })),
                  limits: {
                    drawEntities: 1000,
                    solidPrimitives: 250,
                    inspectEntities: 5000,
                    measureHandles: 500,
                    workflowSteps: 20,
                    eventJournal: 4096,
                  },
                  coordinates:
                    "Drawing units; draw uses current UCS by default, cad_solid uses WCS; angles are radians.",
                  verifiedExports: ["dwg", "dxf", "stl"],
                  confirmedNativeExports: ["pdf", "dwf", "svg", "png", "jpeg"],
                  synchronization: {
                    changeJournal: true,
                    documentRevision: true,
                    optimisticConcurrency: true,
                    visualModes: ["silent", "batch", "step"],
                    commandCompletionEvents: true,
                  },
                  structuredEntities: [
                    "line", "polyline", "polyline3d", "spline", "ray", "xline",
                    "circle", "arc", "ellipse", "point", "text", "mtext", "hatch",
                    "blockReference", "leader", "table", "alignedDimension",
                    "rotatedDimension", "radialDimension", "diametricDimension",
                    "angularDimension2Line",
                  ],
                  solidPrimitives: ["box", "sphere", "cylinder", "cone", "torus", "wedge", "pyramid"],
                  resources: ["cad://standards/ventilation-duty-standby"],
                }
              : tool.method === "__local_connect"
                ? {
                    connected: bridge.selectProcess(
                      (args as { processId: number }).processId,
                    ),
                  }
                : tool.method === "__local_model_build"
                  ? await buildModelRecipe(bridge, args)
                  : tool.method === "__local_workflow"
                    ? await workflow.execute(args, extra.signal)
                    : await bridge.call(tool.method, args, { signal: extra.signal });

        await safeAudit({
          timestamp: new Date().toISOString(),
          requestId,
          tool: tool.name,
          status: "succeeded",
          durationMs: Date.now() - startedAt,
        });

        return {
          content: [{ type: "text" as const, text: JSON.stringify(result, null, 2) }],
          structuredContent:
            result && typeof result === "object"
              ? (result as Record<string, unknown>)
              : { value: result },
        };
      } catch (error) {
        const known = error instanceof CadBridgeError || error instanceof CadWorkflowError;
        const code = known ? error.code : "INTERNAL_ERROR";
        const message = error instanceof Error ? error.message : String(error);
        const details = known ? error.details : undefined;

        await safeAudit({
          timestamp: new Date().toISOString(),
          requestId,
          tool: tool.name,
          status: "failed",
          durationMs: Date.now() - startedAt,
          error: { message, code },
        });

        return {
          isError: true,
          content: [
            {
              type: "text" as const,
              text: JSON.stringify({ error: code, message, details }, null, 2),
            },
          ],
        };
      }
    },
  );
}

await server.connect(new StdioServerTransport());
