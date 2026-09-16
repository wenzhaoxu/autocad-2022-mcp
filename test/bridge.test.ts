import net from "node:net";
import { afterEach, describe, expect, it } from "vitest";
import { z } from "zod";
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
  it("rejects pre-cancelled requests", async () => {
    const controller = new AbortController();
    controller.abort();

    await expect(
      new CadBridgeClient("unused", 1000).call("status", {}, { signal: controller.signal }),
    ).rejects.toMatchObject({ code: "CANCELLED" });
  });

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

  it("preserves CJK text across the UTF-8 named-pipe protocol", async () => {
    const pipe = `cad-mcp-test-${process.pid}-cjk`;
    const source = "车间工艺通风：一号风机运行反馈";
    await mockBridge(pipe, (request) => ({
      id: request.id,
      ok: true,
      result: { echoed: (request.params as { text: string }).text },
    }));

    await expect(
      new CadBridgeClient(pipe, 1000).call("echo", { text: source }),
    ).resolves.toEqual({ echoed: source });
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
    const document = toolDefinitions.find((tool) => tool.name === "cad_document");
    expect(document!.schema.password.safeParse("drawing-password").success).toBe(true);
    expect(document!.schema.password.safeParse("").success).toBe(false);
    const workflow = toolDefinitions.find((tool) => tool.name === "cad_workflow");
    expect(workflow).toBeDefined();
    expect(
      z.object(workflow!.schema).safeParse({
        mode: "preview",
        workflowId: "wf",
        steps: [{ id: "inspect", tool: "cad_inspect", arguments: { scope: "drawing" } }],
      }).success,
    ).toBe(true);

    expect(draw!.schema.entities.safeParse([{
      type: "mtext",
      position: [0, 0],
      text: "双风机一用一备控制原理图",
      height: 5,
      width: 120,
      style: "MCP_CJK",
    }]).success).toBe(true);

    const textStyle = toolDefinitions.find((tool) => tool.name === "cad_text_style");
    expect(textStyle).toBeDefined();
    expect(textStyle!.schema.action.safeParse("create").success).toBe(true);
    expect(textStyle!.schema.name.safeParse("中文工程字体").success).toBe(true);
    expect(textStyle!.schema.fontFile.safeParse("msyh.ttc").success).toBe(true);
    expect(textStyle!.schema.target.safeParse({ filter: { types: ["TEXT", "MTEXT"] } }).success).toBe(true);

    const events = toolDefinitions.find((tool) => tool.name === "cad_events");
    expect(events).toBeDefined();
    expect(z.object(events!.schema).safeParse({ afterSequence: 12, limit: 100, types: ["entity.modified"] }).success).toBe(true);

    const electricalSymbol = toolDefinitions.find((tool) => tool.name === "cad_electrical_symbol");
    expect(electricalSymbol).toBeDefined();
    expect(z.object(electricalSymbol!.schema).safeParse({ symbolId: "motor-3phase", insertionPoint: [100, 50], tag: "M1" }).success).toBe(true);

    const electricalSheet = toolDefinitions.find((tool) => tool.name === "cad_electrical_sheet");
    expect(electricalSheet).toBeDefined();
    expect(z.object(electricalSheet!.schema).safeParse({ sheet: "A3", zoneColumns: 8, zoneRows: 6 }).success).toBe(true);
    expect(z.object(electricalSheet!.schema).safeParse({ sheet: "A3", zoneColumns: 7 }).success).toBe(false);

    expect(draw!.schema.entities.safeParse([
      { type: "spline", fitPoints: [[0, 0], [10, 5], [20, 0]], degree: 2 },
      { type: "table", position: [0, 0], rows: [["名称", "数量"], ["风机", "2"]] },
      { type: "radialDimension", center: [0, 0], chordPoint: [10, 0] },
    ]).success).toBe(true);
    expect(draw!.schema.visualMode.safeParse("step").success).toBe(true);
    expect(draw!.schema.expectedRevision.safeParse(42).success).toBe(true);

    expect(solid!.schema.primitives.safeParse([
      { type: "torus", center: [0, 0, 0], majorRadius: 20, minorRadius: 4 },
      { type: "wedge", origin: [0, 0, 0], length: 20, width: 10, height: 8 },
    ]).success).toBe(true);
  });
});
