import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { afterEach, describe, expect, it } from "vitest";
import { CadBridgeClient, CadBridgeError } from "../src/bridge.js";
import { listCadInstances } from "../src/instances.js";

const temporaryDirectories: string[] = [];
const previousDirectory = process.env.AUTOCAD_MCP_INSTANCE_DIR;

afterEach(() => {
  process.env.AUTOCAD_MCP_INSTANCE_DIR = previousDirectory;
  for (const directory of temporaryDirectories.splice(0)) fs.rmSync(directory, { recursive: true, force: true });
});

function registry(): string {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), "cad-mcp-instances-"));
  temporaryDirectories.push(directory);
  return directory;
}

function add(directory: string, processId: number, pipeName = `autocad-2024-mcp-${processId}`): void {
  fs.writeFileSync(path.join(directory, `${processId}.json`), JSON.stringify({
    processId, pipeName, product: "AutoCAD 2024", startedAt: new Date().toISOString(), updatedAt: new Date().toISOString(), documentName: `Drawing-${processId}.dwg`,
  }));
}

describe("AutoCAD instance discovery", () => {
  it("ignores dead and malformed registrations", () => {
    const directory = registry();
    add(directory, 101);
    add(directory, 202);
    fs.writeFileSync(path.join(directory, "broken.json"), "{");
    expect(listCadInstances(directory, (pid) => pid === 202).map((item) => item.processId)).toEqual([202]);
  });

  it("ignores a stale registration even when its pid has been reused", () => {
    const directory = registry();
    add(directory, process.pid);
    const file = path.join(directory, `${process.pid}.json`);
    const stale = JSON.parse(fs.readFileSync(file, "utf8"));
    stale.updatedAt = "2020-01-01T00:00:00.000Z";
    fs.writeFileSync(file, JSON.stringify(stale));

    expect(listCadInstances(directory, () => true)).toEqual([]);
    expect(fs.existsSync(file)).toBe(false);
  });

  it("requires explicit selection when multiple processes are live", async () => {
    const directory = registry();
    add(directory, process.pid, "unreachable-a");
    add(directory, process.ppid, "unreachable-b");
    process.env.AUTOCAD_MCP_INSTANCE_DIR = directory;
    const client = new CadBridgeClient(undefined, 50);
    await expect(client.call("status", {})).rejects.toMatchObject({ code: "MULTIPLE_AUTOCAD_INSTANCES" } satisfies Partial<CadBridgeError>);
  });

  it("binds to an explicitly selected live process", () => {
    const directory = registry();
    add(directory, process.pid);
    process.env.AUTOCAD_MCP_INSTANCE_DIR = directory;
    const selected = new CadBridgeClient(undefined, 50).selectProcess(process.pid);
    expect(selected.pipeName).toBe(`autocad-2024-mcp-${process.pid}`);
  });
});
