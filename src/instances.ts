import fs from "node:fs";
import os from "node:os";
import path from "node:path";

export interface CadInstance {
  processId: number;
  pipeName: string;
  product: string;
  startedAt: string;
  updatedAt: string;
  documentName?: string | null;
  documentPath?: string | null;
}

export function instanceDirectory(): string {
  const localAppData = process.env.LOCALAPPDATA ?? path.join(os.homedir(), "AppData", "Local");
  return process.env.AUTOCAD_MCP_INSTANCE_DIR ?? path.join(localAppData, "AutoCAD2024Mcp", "instances");
}

export function isProcessAlive(processId: number): boolean {
  try {
    process.kill(processId, 0);
    return true;
  } catch {
    return false;
  }
}

export function listCadInstances(
  directory = instanceDirectory(),
  alive: (processId: number) => boolean = isProcessAlive,
): CadInstance[] {
  if (!fs.existsSync(directory)) return [];
  const instances: CadInstance[] = [];
  for (const name of fs.readdirSync(directory)) {
    if (!name.endsWith(".json")) continue;
    try {
      const value = JSON.parse(fs.readFileSync(path.join(directory, name), "utf8")) as CadInstance;
      if (!Number.isInteger(value.processId) || !value.pipeName || !alive(value.processId)) {
        try { fs.unlinkSync(path.join(directory, name)); } catch { }
        continue;
      }
      instances.push(value);
    } catch {
      // Ignore partial registrations. The owning AutoCAD process rewrites them atomically.
    }
  }
  return instances.sort((a, b) => a.processId - b.processId);
}
