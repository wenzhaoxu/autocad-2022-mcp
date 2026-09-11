import net from "node:net";
import { afterEach, describe, expect, it } from "vitest";
import { CadBridgeClient, CadBridgeError, type BridgeRequest } from "../src/bridge.js";
import { toolDefinitions } from "../src/tools.js";

const servers: net.Server[] = [];
afterEach(async () => {
  await Promise.all(servers.splice(0).map((server) => new Promise<void>((resolve) => server.close(() => resolve()))));
});

function mockBridge(name: string, handler: (request: BridgeRequest) => unknown): Promise<void> {
  return new Promise((resolve, reject) => {
    const server = net.createServer((socket) => {
      socket.setEncoding("utf8");
      let data = "";
      socket.on("data", (chunk) => {
        data += chunk;
        const newline = data.indexOf("\n");
        if (newline < 0) return;
        const request = JSON.parse(data.slice(0, newline)) as BridgeRequest;
        socket.end(`${JSON.stringify(handler(request))}\n`);
      });
    });
    servers.push(server);
    server.once("error", reject);
    server.listen(`\\\\.\\pipe\\${name}`, resolve);
  });
}

describe("CadBridgeClient", () => {
  it("round-trips a request and validates its id", async () => {
    const pipe = `cad-mcp-test-${process.pid}-ok`;
    await mockBridge(pipe, (request) => ({ id: request.id, ok: true, result: { method: request.method } }));
    await expect(new CadBridgeClient(pipe, 1000).call("status", {})).resolves.toEqual({ method: "status" });
  });

  it("surfaces structured CAD errors", async () => {
    const pipe = `cad-mcp-test-${process.pid}-error`;
    await mockBridge(pipe, (request) => ({ id: request.id, ok: false, error: { code: "NO_ACTIVE_DOCUMENT", message: "No drawing" } }));
    await expect(new CadBridgeClient(pipe, 1000).call("inspect", {})).rejects.toMatchObject({ code: "NO_ACTIVE_DOCUMENT", message: "No drawing" } satisfies Partial<CadBridgeError>);
  });
});

describe("tool catalog", () => {
  it("has unique names and parseable representative payloads", () => {
    expect(new Set(toolDefinitions.map((tool) => tool.name)).size).toBe(toolDefinitions.length);
    const draw = toolDefinitions.find((tool) => tool.name === "cad_draw");
    expect(draw).toBeDefined();
    expect(draw!.schema.entities.safeParse([{ type: "line", start: [0, 0], end: [10, 10] }]).success).toBe(true);
    const solid = toolDefinitions.find((tool) => tool.name === "cad_solid");
    expect(solid!.schema.primitives.safeParse([{ type: "box", origin: [0, 0, 0], length: 10, width: 8, height: 6 }]).success).toBe(true);
  });
});
