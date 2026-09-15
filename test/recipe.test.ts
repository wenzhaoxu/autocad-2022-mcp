import { describe, expect, it } from "vitest";
import { buildModelRecipe, type RecipeBridge } from "../src/recipe.js";

class FakeBridge implements RecipeBridge {
  readonly calls: Array<{ method: string; params: any }> = [];
  private nextHandle = 1;
  failBoolean = false;

  async call<T = unknown>(method: string, params: any = {}): Promise<T> {
    this.calls.push({ method, params });
    if (method === "solid" && params.action === "create") {
      return {
        created: params.primitives.map(() => ({ handle: (this.nextHandle++).toString(16).toUpperCase() })),
      } as T;
    }
    if (method === "solid" && params.action === "boolean") {
      if (this.failBoolean) throw new Error("boolean failed");
      return { handle: params.targetHandle } as T;
    }
    if (method === "edit") return { ok: true } as T;
    throw new Error(`Unexpected bridge method: ${method}`);
  }
}

const ref = (param: string, scale = 1, offset = 0) => ({ param, scale, offset });

describe("parameterized model recipes", () => {
  it("prevalidates and batches adjacent primitives into one CAD transaction", async () => {
    const bridge = new FakeBridge();
    const result = await buildModelRecipe(bridge, {
      recipe: {
        id: "batch",
        units: "mm",
        parameters: { size: 10 },
        features: [
          { id: "body", type: "box", length: ref("size", 2), width: ref("size"), height: 4, layer: "MODEL" },
          { id: "pin", type: "cylinder", center: [0, 0, 0], radius: 2, height: ref("size"), axis: [1, 0, 0], colorIndex: 3 },
          { id: "cap", type: "sphere", center: [20, 0, 0], radius: 3 },
        ],
      },
      overrides: { size: 12 },
    });

    expect(bridge.calls).toHaveLength(1);
    expect(bridge.calls[0]).toMatchObject({ method: "solid", params: { action: "create" } });
    expect(bridge.calls[0].params.primitives).toHaveLength(3);
    expect(bridge.calls[0].params.primitives[0]).toMatchObject({ length: 24, width: 12, layer: "MODEL" });
    expect(bridge.calls[0].params.primitives[1]).toMatchObject({ axis: [1, 0, 0], colorIndex: 3 });
    expect(result.liveHandles).toEqual(["1", "2", "3"]);
    expect(result.parameters.size).toBe(12);
  });

  it("executes booleans after their primitive batch and removes previous model handles", async () => {
    const bridge = new FakeBridge();
    const result = await buildModelRecipe(bridge, {
      recipe: {
        id: "cut-body",
        units: "mm",
        parameters: {},
        features: [
          { id: "body", type: "box", length: 20, width: 20, height: 20 },
          { id: "hole", type: "cylinder", radius: 4, height: 20 },
          { id: "finished", type: "boolean", operation: "subtract", target: "body", tools: ["hole"] },
        ],
      },
      previousHandles: ["AA", "BB", "AA"],
    });

    expect(bridge.calls.map((call) => `${call.method}:${call.params.action}`)).toEqual([
      "solid:create",
      "solid:boolean",
      "edit:undefined",
    ]);
    expect(bridge.calls[1].params).toMatchObject({ targetHandle: "1", toolHandles: ["2"] });
    expect(bridge.calls[2].params.target.handles).toEqual(["AA", "BB"]);
    expect(result.liveHandles).toEqual(["1"]);
    expect(result.featureHandles.finished).toBe("1");
  });

  it("rejects an invalid recipe before creating any CAD entity", async () => {
    const bridge = new FakeBridge();
    await expect(buildModelRecipe(bridge, {
      recipe: {
        id: "invalid",
        units: "mm",
        parameters: { size: -1 },
        features: [{ id: "body", type: "box", length: ref("size"), width: 2, height: 2 }],
      },
    })).rejects.toThrow("body.length must resolve to a positive number");
    expect(bridge.calls).toHaveLength(0);
  });

  it("erases newly created entities when a later boolean fails", async () => {
    const bridge = new FakeBridge();
    bridge.failBoolean = true;
    await expect(buildModelRecipe(bridge, {
      recipe: {
        id: "rollback",
        units: "mm",
        parameters: {},
        features: [
          { id: "body", type: "box", length: 10, width: 10, height: 10 },
          { id: "tool", type: "sphere", radius: 2 },
          { id: "result", type: "boolean", operation: "subtract", target: "body", tools: ["tool"] },
        ],
      },
    })).rejects.toThrow("boolean failed");

    expect(bridge.calls.at(-1)).toMatchObject({
      method: "edit",
      params: { target: { handles: ["1", "2"] }, operations: [{ op: "erase" }] },
    });
  });
});
