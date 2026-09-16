import { appendFile, mkdir } from "node:fs/promises";
import { dirname } from "node:path";

const SECRET_KEY = /(password|token|secret|authorization|api[-_]?key|credential)/i;

export function redactSecrets(value: unknown): unknown {
  if (Array.isArray(value)) return value.map(redactSecrets);
  if (!value || typeof value !== "object") return value;
  return Object.fromEntries(
    Object.entries(value as Record<string, unknown>).map(([key, child]) => [
      key,
      SECRET_KEY.test(key) ? "[REDACTED]" : redactSecrets(child),
    ]),
  );
}

export interface AuditRecord {
  timestamp: string;
  requestId: string;
  tool: string;
  status: "started" | "succeeded" | "failed";
  durationMs?: number;
  arguments?: unknown;
  error?: { message: string; code?: string };
}

export class AuditLogger {
  constructor(private readonly logPath = process.env.AUTOCAD_MCP_AUDIT_LOG) {}

  get enabled(): boolean {
    return Boolean(this.logPath);
  }

  async write(record: AuditRecord): Promise<void> {
    if (!this.logPath) return;
    await mkdir(dirname(this.logPath), { recursive: true });
    const safe = redactSecrets(record);
    await appendFile(this.logPath, `${JSON.stringify(safe)}\n`, "utf8");
  }
}
