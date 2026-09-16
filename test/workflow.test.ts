import { describe, expect, it } from "vitest";
import { toolDefinitions } from "../src/tools.js";
import { CadWorkflowExecutor, type WorkflowBridge } from "../src/workflow.js";

class FakeBridge implements WorkflowBridge {
  calls: Array<{ method: string; params: Record<string, unknown> }> = [];
  inspectTotal = 1;
  documentId = "D1";

  async call(method: string, params: unknown = {}): Promise<unknown> {
    const objectParams = params as Record<string, unknown>;
    this.calls.push({ method, params: objectParams });
    if (method === "status") {
      return {
        activeDocument: {
          id: this.documentId,
          name: "Drawing1.dwg",
          path: "D:/Drawing1.dwg",
          readOnly: false,
          revision: 7,
        },
      };
    }
    if (method === "draw") return { created: [{ handle: "A" }] };
    if (method === "inspect") {
      return {
        total: this.inspectTotal,
        entities: this.inspectTotal ? [{ handle: "A" }] : [],
      };
    }
    if (method === "undo") return { action: "undo", steps: objectParams.steps };
    return {};
  }
}

function payload(mode: "preview" | "apply" = "apply") {
  return {
    mode,
    workflowId: "wf",
    idempotencyKey: "key",
    expectedDocument: { id: "D1", requireWritable: true },
    steps: [{
      id: "draw",
      tool: "cad_draw",
      arguments: { entities: [{ type: "line", start: [0, 0], end: [1, 1] }] },
    }],
    verification: [{
      id: "verify",
      tool: "cad_inspect",
      arguments: { scope: "entities", handles: ["A"] },
      expect: { minCount: 1 },
    }],
  };
}

describe("CAD workflow executor", () => {
  it("previews without applying mutations", async () => {
    const bridge = new FakeBridge();
    const executor = new CadWorkflowExecutor(bridge, toolDefinitions);
    const result = (await executor.execute(payload("preview"))) as {
      plan: Array<Record<string, unknown>>;
      mutationCount: number;
      mode: string;
    };

    expect(result).toMatchObject({ mode: "preview", mutationCount: 1 });
    expect(result.plan[0]).toMatchObject({ tool: "cad_draw", mutatesDrawing: true });
    expect(bridge.calls.map((call) => call.method)).toEqual(["status"]);
  });

  it("replays an idempotent result without drawing twice", async () => {
    const bridge = new FakeBridge();
    const executor = new CadWorkflowExecutor(bridge, toolDefinitions);

    await executor.execute(payload());
    const replay = (await executor.execute(payload())) as { replayed: boolean };

    expect(replay.replayed).toBe(true);
    expect(bridge.calls.filter((call) => call.method === "draw")).toHaveLength(1);
  });

  it("propagates a workflow visual mode to drawing mutations", async () => {
    const bridge = new FakeBridge();
    const executor = new CadWorkflowExecutor(bridge, toolDefinitions);
    await executor.execute({ ...payload(), idempotencyKey: "visual", visualMode: "step" });

    expect(bridge.calls.find((call) => call.method === "draw")?.params).toMatchObject({
      visualMode: "step",
      expectedDocumentId: "D1",
      expectedRevision: 7,
    });
  });

  it("uses compensating undo after failed verification", async () => {
    const bridge = new FakeBridge();
    bridge.inspectTotal = 0;
    const executor = new CadWorkflowExecutor(bridge, toolDefinitions);

    await expect(executor.execute(payload())).rejects.toMatchObject({
      code: "WORKFLOW_FAILED",
    });
    expect(bridge.calls.find((call) => call.method === "undo")).toMatchObject({
      params: { action: "undo", steps: 1 },
    });
  });

  it("rejects a document mismatch before drawing", async () => {
    const bridge = new FakeBridge();
    bridge.documentId = "D2";
    const executor = new CadWorkflowExecutor(bridge, toolDefinitions);

    await expect(executor.execute(payload())).rejects.toMatchObject({
      code: "WORKFLOW_FAILED",
    });
    expect(bridge.calls.some((call) => call.method === "draw")).toBe(false);
  });
});
