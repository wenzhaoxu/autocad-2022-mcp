import net from "node:net";
import { randomUUID } from "node:crypto";
import { listCadInstances, type CadInstance } from "./instances.js";

export interface BridgeRequest {
  id: string;
  method: string;
  params: unknown;
  timeoutMs: number;
}

export interface BridgeError {
  code: string;
  message: string;
  details?: unknown;
}

export interface BridgeResponse<T = unknown> {
  id: string;
  ok: boolean;
  result?: T;
  error?: BridgeError;
}

export interface BridgeCallOptions {
  signal?: AbortSignal;
  timeoutMs?: number;
}

export class CadBridgeError extends Error {
  constructor(
    message: string,
    public readonly code: string,
    public readonly details?: unknown,
  ) {
    super(message);
    this.name = "CadBridgeError";
  }
}

export class CadBridgeClient {
  private selectedProcessId: number | undefined;

  constructor(
    private readonly explicitPipeName = process.env.AUTOCAD_MCP_PIPE,
    private readonly timeoutMs = Number(process.env.AUTOCAD_MCP_TIMEOUT_MS ?? 120_000),
  ) {
    const configuredPid = Number(process.env.AUTOCAD_MCP_PID);
    if (Number.isInteger(configuredPid) && configuredPid > 0) this.selectedProcessId = configuredPid;
  }

  listInstances(): CadInstance[] {
    return listCadInstances();
  }

  selectProcess(processId: number): CadInstance {
    const instance = this.listInstances().find((item) => item.processId === processId);
    if (!instance) throw new CadBridgeError(`AutoCAD process ${processId} is not registered`, "INSTANCE_NOT_FOUND", { processId });
    this.selectedProcessId = processId;
    return instance;
  }

  private resolvePipeName(): string {
    if (this.explicitPipeName) return this.explicitPipeName;
    const instances = this.listInstances();
    if (this.selectedProcessId) {
      const selected = instances.find((item) => item.processId === this.selectedProcessId);
      if (!selected) throw new CadBridgeError(`Selected AutoCAD process ${this.selectedProcessId} is no longer available`, "INSTANCE_NOT_FOUND", { processId: this.selectedProcessId, instances });
      return selected.pipeName;
    }
    if (instances.length === 1) return instances[0].pipeName;
    if (instances.length === 0) throw new CadBridgeError("AutoCAD 2024 bridge is not running. Start AutoCAD and load AutoCAD2024Mcp.", "BRIDGE_UNAVAILABLE");
    throw new CadBridgeError("Multiple AutoCAD instances are available. Call cad_connect with the intended processId.", "MULTIPLE_AUTOCAD_INSTANCES", { instances });
  }

  async call<T = unknown>(
    method: string,
    params: unknown = {},
    options: BridgeCallOptions = {},
  ): Promise<T> {
    const signal = options.signal;
    if (signal?.aborted) {
      throw new CadBridgeError("AutoCAD bridge request was cancelled", "CANCELLED");
    }

    const timeoutMs = options.timeoutMs ?? this.timeoutMs;
    const request: BridgeRequest = { id: randomUUID(), method, params, timeoutMs };
    const pipePath = `\\\\.\\pipe\\${this.resolvePipeName()}`;

    return await new Promise<T>((resolve, reject) => {
      const socket = net.createConnection(pipePath);
      let settled = false;
      let data = "";
      let timer: NodeJS.Timeout | undefined;
      let onAbort: (() => void) | undefined;

      const finish = (error?: Error, result?: T) => {
        if (settled) return;
        settled = true;
        if (timer) clearTimeout(timer);
        if (onAbort) signal?.removeEventListener("abort", onAbort);
        socket.destroy();
        if (error) reject(error);
        else resolve(result as T);
      };

      onAbort = () => {
        finish(new CadBridgeError("AutoCAD bridge request was cancelled", "CANCELLED"));
      };
      signal?.addEventListener("abort", onAbort, { once: true });

      timer = setTimeout(() => {
        finish(new CadBridgeError(`AutoCAD did not respond within ${timeoutMs} ms`, "TIMEOUT"));
      }, timeoutMs);

      socket.setEncoding("utf8");
      socket.once("connect", () => socket.write(`${JSON.stringify(request)}\n`));
      socket.on("data", (chunk) => {
        data += chunk;
        const newline = data.indexOf("\n");
        if (newline < 0) return;

        try {
          const response = JSON.parse(data.slice(0, newline)) as BridgeResponse<T>;
          if (response.id !== request.id) {
            finish(new CadBridgeError("Mismatched bridge response id", "PROTOCOL_ERROR"));
          } else if (!response.ok) {
            finish(new CadBridgeError(
              response.error?.message ?? "AutoCAD bridge request failed",
              response.error?.code ?? "CAD_ERROR",
              response.error?.details,
            ));
          } else {
            finish(undefined, response.result);
          }
        } catch (error) {
          finish(new CadBridgeError(
            `Invalid response from AutoCAD: ${error instanceof Error ? error.message : String(error)}`,
            "PROTOCOL_ERROR",
          ));
        }
      });
      socket.once("error", (error) => {
        const message = error.message.includes("ENOENT")
          ? "AutoCAD 2024 bridge is not running. Start AutoCAD and load AutoCAD2024Mcp.dll."
          : error.message;
        finish(new CadBridgeError(message, "BRIDGE_UNAVAILABLE"));
      });
      socket.once("end", () => {
        if (!settled) finish(new CadBridgeError("AutoCAD closed the bridge connection without a response", "PROTOCOL_ERROR"));
      });
    });
  }
}
