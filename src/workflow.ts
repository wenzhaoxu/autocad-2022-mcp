import { z } from "zod";

const workflowToolNames = [
  "cad_inspect",
  "cad_measure",
  "cad_solid",
  "cad_draw",
  "cad_edit",
  "cad_layer",
  "cad_block",
  "cad_layout",
  "cad_view",
] as const;

const verificationToolNames = ["cad_inspect", "cad_measure"] as const;

const expectedDocumentSchema = z
  .object({
    id: z.string().min(1).optional(),
    name: z.string().min(1).optional(),
    path: z.string().min(1).optional(),
    revision: z.number().int().nonnegative().optional(),
    requireWritable: z.boolean().optional(),
  })
  .strict();

const expectationSchema = z
  .object({
    minCount: z.number().int().nonnegative().optional(),
    maxCount: z.number().int().nonnegative().optional(),
    equals: z.record(z.union([z.string(), z.number(), z.boolean(), z.null()])).optional(),
    truthy: z.array(z.string().min(1)).optional(),
  })
  .strict();

export const workflowInputSchema = z
  .object({
    mode: z.enum(["preview", "apply"]),
    workflowId: z.string().min(1).max(128),
    idempotencyKey: z.string().min(1).max(128).optional(),
    expectedDocument: expectedDocumentSchema.optional(),
    visualMode: z.enum(["silent", "batch", "step"]).optional(),
    steps: z
      .array(
        z
          .object({
            id: z.string().min(1).max(128),
            tool: z.enum(workflowToolNames),
            arguments: z.record(z.unknown()),
          })
          .strict(),
      )
      .min(1)
      .max(20),
    verification: z
      .array(
        z
          .object({
            id: z.string().min(1).max(128),
            tool: z.enum(verificationToolNames),
            arguments: z.record(z.unknown()),
            expect: expectationSchema.optional(),
          })
          .strict(),
      )
      .max(20)
      .optional(),
    rollbackOnFailure: z.boolean().default(true),
  })
  .strict();

export type WorkflowInput = z.infer<typeof workflowInputSchema>;

export interface WorkflowToolDefinition {
  name: string;
  method: string;
  schema: z.ZodRawShape;
  readOnly?: boolean;
  destructive?: boolean;
}

export interface WorkflowBridge {
  call(method: string, params: unknown, options?: { signal?: AbortSignal }): Promise<unknown>;
}

export class CadWorkflowError extends Error {
  readonly code = "WORKFLOW_FAILED";
  constructor(message: string, readonly details: Record<string, unknown>) {
    super(message);
    this.name = "CadWorkflowError";
  }
}

function valueAtPath(value: unknown, path: string): unknown {
  return path.split(".").reduce<unknown>((current, part) => {
    if (!current || typeof current !== "object") return undefined;
    return (current as Record<string, unknown>)[part];
  }, value);
}

function inferredCount(value: unknown): number | undefined {
  if (!value || typeof value !== "object") return undefined;
  const object = value as Record<string, unknown>;
  if (typeof object.total === "number") return object.total;
  if (typeof object.count === "number") return object.count;
  for (const key of ["entities", "items", "results", "measurements", "layers"]) {
    if (Array.isArray(object[key])) return object[key].length;
  }
  return undefined;
}

function verifyExpectation(result: unknown, expect: z.infer<typeof expectationSchema> | undefined): string[] {
  if (!expect) return [];
  const failures: string[] = [];
  const count = inferredCount(result);
  if (expect.minCount !== undefined && (count === undefined || count < expect.minCount)) {
    failures.push(`count must be >= ${expect.minCount}; received ${String(count)}`);
  }
  if (expect.maxCount !== undefined && (count === undefined || count > expect.maxCount)) {
    failures.push(`count must be <= ${expect.maxCount}; received ${String(count)}`);
  }
  for (const [path, expected] of Object.entries(expect.equals ?? {})) {
    const actual = valueAtPath(result, path);
    if (!Object.is(actual, expected)) failures.push(`${path} must equal ${JSON.stringify(expected)}`);
  }
  for (const path of expect.truthy ?? []) {
    if (!valueAtPath(result, path)) failures.push(`${path} must be truthy`);
  }
  return failures;
}

function documentView(status: unknown): Record<string, unknown> {
  if (!status || typeof status !== "object") return {};
  const value = status as Record<string, unknown>;
  const nested = value.activeDocument ?? value.document;
  return nested && typeof nested === "object" ? (nested as Record<string, unknown>) : value;
}

const DATABASE_MUTATION_METHODS = new Set(["solid", "draw", "edit", "layer", "block", "layout"]);

function isDatabaseMutation(definition: WorkflowToolDefinition): boolean {
  return DATABASE_MUTATION_METHODS.has(definition.method);
}

function assertDocument(status: unknown, expected: WorkflowInput["expectedDocument"]): void {
  if (!expected) return;
  const document = documentView(status);
  for (const key of ["id", "name", "path"] as const) {
    if (expected[key] !== undefined && document[key] !== expected[key]) {
      throw new CadWorkflowError("Active document does not match workflow precondition", {
        phase: "preflight",
        field: key,
        expected: expected[key],
        actual: document[key],
      });
    }
  }
  if (expected.requireWritable && (document.readOnly === true || document.isReadOnly === true)) {
    throw new CadWorkflowError("Active document is read-only", { phase: "preflight" });
  }
}

export class CadWorkflowExecutor {
  private readonly catalog: Map<string, WorkflowToolDefinition>;
  private readonly successCache = new Map<string, unknown>();

  constructor(
    private readonly bridge: WorkflowBridge,
    definitions: WorkflowToolDefinition[],
  ) {
    this.catalog = new Map(definitions.map((definition) => [definition.name, definition]));
  }

  async execute(raw: unknown, signal?: AbortSignal): Promise<unknown> {
    const input = workflowInputSchema.parse(raw);
    const steps = input.steps.map((step) => {
      const definition = this.catalog.get(step.tool);
      const argumentsWithPresentation = input.visualMode && definition && isDatabaseMutation(definition) && step.arguments.visualMode === undefined
        ? { ...step.arguments, visualMode: input.visualMode }
        : step.arguments;
      return { ...step, arguments: argumentsWithPresentation, definition: this.validateTool(step.tool, argumentsWithPresentation) };
    });
    const verification = (input.verification ?? []).map((step) => ({
      ...step,
      definition: this.validateTool(step.tool, step.arguments),
    }));
    const duplicateIds = [...input.steps, ...(input.verification ?? [])]
      .map((item) => item.id)
      .filter((id, index, all) => all.indexOf(id) !== index);
    if (duplicateIds.length) {
      throw new CadWorkflowError("Workflow step ids must be unique", { duplicateIds: [...new Set(duplicateIds)] });
    }

    if (input.mode === "apply" && input.idempotencyKey && this.successCache.has(input.idempotencyKey)) {
      return { ...(this.successCache.get(input.idempotencyKey) as Record<string, unknown>), replayed: true };
    }

    const status = await this.bridge.call("status", {}, { signal });
    assertDocument(status, input.expectedDocument);
    const mutationCount = steps.filter(({ definition }) => isDatabaseMutation(definition)).length;
    const preview = {
      workflowId: input.workflowId,
      mode: input.mode,
      document: documentView(status),
      stepCount: steps.length,
      mutationCount,
      verificationCount: verification.length,
      risks: [
        ...(mutationCount > 0 && verification.length === 0 ? ["Mutating workflow has no explicit verification"] : []),
        ...(steps.some(({ definition }) => definition.destructive) ? ["Workflow contains destructive operations"] : []),
      ],
      plan: steps.map((step) => ({
          id: step.id,
          tool: step.tool,
          readOnly: Boolean(step.definition.readOnly),
          mutatesDrawing: isDatabaseMutation(step.definition),
        })),
    };
    if (input.mode === "preview") return preview;

    const completed: Array<{ id: string; tool: string; result: unknown }> = [];
    const initialDocument = documentView(status);
    const activeDocumentId = typeof initialDocument.id === "string" ? initialDocument.id : undefined;
    let activeRevision = typeof initialDocument.revision === "number" ? initialDocument.revision : undefined;
    try {
      for (const step of steps) {
        const runtimeArguments = isDatabaseMutation(step.definition)
          ? {
              ...step.arguments,
              ...(activeDocumentId && step.arguments.expectedDocumentId === undefined ? { expectedDocumentId: activeDocumentId } : {}),
              ...(activeRevision !== undefined && step.arguments.expectedRevision === undefined ? { expectedRevision: activeRevision } : {}),
            }
          : step.arguments;
        const result = await this.bridge.call(step.definition.method, runtimeArguments, { signal });
        if (result && typeof result === "object" && typeof (result as Record<string, unknown>).revision === "number") {
          activeRevision = (result as Record<string, number>).revision;
        }
        completed.push({ id: step.id, tool: step.tool, result });
      }
      const checks: Array<{ id: string; tool: string; result: unknown }> = [];
      for (const check of verification) {
        const result = await this.bridge.call(check.definition.method, check.arguments, { signal });
        const failures = verifyExpectation(result, check.expect);
        if (failures.length) {
          throw new CadWorkflowError(`Verification ${check.id} failed`, {
            phase: "verification",
            verificationId: check.id,
            failures,
            result,
          });
        }
        checks.push({ id: check.id, tool: check.tool, result });
      }
      const response = { ...preview, mode: "apply", applied: true, replayed: false, completed, verification: checks };
      if (input.idempotencyKey) {
        if (this.successCache.size >= 100) this.successCache.delete(this.successCache.keys().next().value as string);
        this.successCache.set(input.idempotencyKey, response);
      }
      return response;
    } catch (error) {
      let rollback: unknown = { requested: false };
      const completedMutations = completed.filter((item) => {
        const definition = this.catalog.get(item.tool);
        return definition ? isDatabaseMutation(definition) : false;
      }).length;
      if (input.rollbackOnFailure && completedMutations > 0) {
        try {
          rollback = await this.bridge.call("undo", { action: "undo", steps: completedMutations });
        } catch (rollbackError) {
          rollback = { requested: true, failed: true, message: String(rollbackError) };
        }
      }
      if (error instanceof CadWorkflowError) {
        throw new CadWorkflowError(error.message, { ...error.details, completed, rollback });
      }
      throw new CadWorkflowError(error instanceof Error ? error.message : String(error), {
        phase: "execution",
        completed,
        rollback,
      });
    }
  }

  private validateTool(name: string, args: Record<string, unknown>): WorkflowToolDefinition {
    const definition = this.catalog.get(name);
    if (!definition || !workflowToolNames.includes(name as (typeof workflowToolNames)[number])) {
      throw new CadWorkflowError(`Tool ${name} is not allowed in a workflow`, { tool: name });
    }
    z.object(definition.schema).strict().parse(args);
    return definition;
  }
}
