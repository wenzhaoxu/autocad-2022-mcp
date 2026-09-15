import { z } from "zod";

const scalarSchema = z.union([
  z.number().finite(),
  z.object({
    param: z.string().min(1),
    scale: z.number().finite().optional(),
    offset: z.number().finite().optional(),
  }).strict(),
]);

const pointSchema = z.tuple([scalarSchema, scalarSchema, scalarSchema]);

const commonFeature = {
  id: z.string().min(1),
};

const primitiveProperties = {
  layer: z.string().min(1).optional(),
  colorIndex: z.number().int().min(0).max(256).optional(),
};

const boxFeatureSchema = z.object({
  ...commonFeature,
  type: z.literal("box"),
  origin: pointSchema.optional(),
  length: scalarSchema,
  width: scalarSchema,
  height: scalarSchema,
  ...primitiveProperties,
}).strict();

const sphereFeatureSchema = z.object({
  ...commonFeature,
  type: z.literal("sphere"),
  center: pointSchema.optional(),
  radius: scalarSchema,
  ...primitiveProperties,
}).strict();

const cylinderFeatureSchema = z.object({
  ...commonFeature,
  type: z.literal("cylinder"),
  center: pointSchema.optional(),
  radius: scalarSchema,
  height: scalarSchema,
  axis: pointSchema.optional(),
  ...primitiveProperties,
}).strict();

const coneFeatureSchema = z.object({
  ...commonFeature,
  type: z.literal("cone"),
  center: pointSchema.optional(),
  baseRadius: scalarSchema,
  topRadius: scalarSchema.optional(),
  height: scalarSchema,
  axis: pointSchema.optional(),
  ...primitiveProperties,
}).strict();

const booleanFeatureSchema = z.object({
  ...commonFeature,
  type: z.literal("boolean"),
  operation: z.enum(["union", "subtract", "intersect"]),
  target: z.string().min(1),
  tools: z.array(z.string().min(1)).min(1),
}).strict();

const featureSchema = z.discriminatedUnion("type", [
  boxFeatureSchema,
  sphereFeatureSchema,
  cylinderFeatureSchema,
  coneFeatureSchema,
  booleanFeatureSchema,
]);

export const modelRecipeSchema = z.object({
  id: z.string().min(1),
  units: z.literal("mm").default("mm"),
  parameters: z.record(z.string(), z.number().finite()),
  features: z.array(featureSchema).min(1).max(1000),
}).strict();

export const modelBuildInputSchema = z.object({
  recipe: modelRecipeSchema,
  overrides: z.record(z.string(), z.number().finite()).optional(),
  previousHandles: z.array(z.string().min(1)).max(1000).optional(),
}).strict();

export type ModelBuildInput = z.infer<typeof modelBuildInputSchema>;

export interface RecipeBridge {
  call<T = unknown>(method: string, params?: unknown): Promise<T>;
}

interface CreatedResponse {
  created?: Array<{ handle?: string }>;
}

interface BooleanResponse {
  handle?: string;
}

function resolveScalar(
  value: z.infer<typeof scalarSchema>,
  parameters: Record<string, number>,
): number {
  if (typeof value === "number") return value;
  const base = parameters[value.param];
  if (base === undefined) {
    throw new Error(`Unknown parameter reference: ${value.param}`);
  }
  return base * (value.scale ?? 1) + (value.offset ?? 0);
}

function resolvePoint(
  value: z.infer<typeof pointSchema> | undefined,
  parameters: Record<string, number>,
): [number, number, number] {
  const point = value ?? [0, 0, 0];
  return point.map((item) => resolveScalar(item, parameters)) as [number, number, number];
}

function positive(value: number, name: string): number {
  if (value <= 0) throw new Error(`${name} must resolve to a positive number`);
  return value;
}

function nonNegative(value: number, name: string): number {
  if (value < 0) throw new Error(`${name} must resolve to a non-negative number`);
  return value;
}

function requireBooleanHandle(response: BooleanResponse, context: string): string {
  const handle = response.handle;
  if (!handle) throw new Error(`${context} did not return an entity handle`);
  return handle;
}

function requireCreatedHandles(response: CreatedResponse, expected: number, context: string): string[] {
  const handles = response.created?.map((item) => item.handle).filter((item): item is string => Boolean(item)) ?? [];
  if (handles.length !== expected) {
    throw new Error(`${context} returned ${handles.length} entity handles; expected ${expected}`);
  }
  return handles;
}

function primitiveEntity(
  feature: Exclude<z.infer<typeof featureSchema>, { type: "boolean" }>,
  parameters: Record<string, number>,
): Record<string, unknown> {
  let entity: Record<string, unknown>;
  switch (feature.type) {
    case "box":
      entity = {
        type: "box",
        origin: resolvePoint(feature.origin, parameters),
        length: positive(resolveScalar(feature.length, parameters), `${feature.id}.length`),
        width: positive(resolveScalar(feature.width, parameters), `${feature.id}.width`),
        height: positive(resolveScalar(feature.height, parameters), `${feature.id}.height`),
      };
      break;
    case "sphere":
      entity = {
        type: "sphere",
        center: resolvePoint(feature.center, parameters),
        radius: positive(resolveScalar(feature.radius, parameters), `${feature.id}.radius`),
      };
      break;
    case "cylinder":
      entity = {
        type: "cylinder",
        center: resolvePoint(feature.center, parameters),
        radius: positive(resolveScalar(feature.radius, parameters), `${feature.id}.radius`),
        height: positive(resolveScalar(feature.height, parameters), `${feature.id}.height`),
        ...(feature.axis ? { axis: resolvePoint(feature.axis, parameters) } : {}),
      };
      break;
    case "cone":
      entity = {
        type: "cone",
        center: resolvePoint(feature.center, parameters),
        baseRadius: positive(resolveScalar(feature.baseRadius, parameters), `${feature.id}.baseRadius`),
        topRadius: nonNegative(resolveScalar(feature.topRadius ?? 0, parameters), `${feature.id}.topRadius`),
        height: positive(resolveScalar(feature.height, parameters), `${feature.id}.height`),
        ...(feature.axis ? { axis: resolvePoint(feature.axis, parameters) } : {}),
      };
      break;
  }
  if (feature.layer !== undefined) entity.layer = feature.layer;
  if (feature.colorIndex !== undefined) entity.colorIndex = feature.colorIndex;
  return entity;
}

async function eraseHandles(bridge: RecipeBridge, handles: Iterable<string>): Promise<void> {
  const unique = [...new Set(handles)].filter(Boolean);
  if (unique.length === 0) return;
  await bridge.call("edit", {
    target: { handles: unique },
    operations: [{ op: "erase" }],
    allowEmpty: true,
  });
}

export async function buildModelRecipe(
  bridge: RecipeBridge,
  rawInput: unknown,
): Promise<{
  modelId: string;
  parameters: Record<string, number>;
  featureHandles: Record<string, string>;
  liveHandles: string[];
  trace: Array<{ featureId: string; type: string; handle: string }>;
  warnings: string[];
}> {
  const input = modelBuildInputSchema.parse(rawInput);
  const parameters = { ...input.recipe.parameters };
  for (const [name, value] of Object.entries(input.overrides ?? {})) {
    if (!(name in parameters)) throw new Error(`Unknown parameter override: ${name}`);
    parameters[name] = value;
  }

  const featureHandles: Record<string, string> = {};
  const liveByFeature = new Map<string, string>();
  const trace: Array<{ featureId: string; type: string; handle: string }> = [];
  const ids = new Set<string>();
  const available = new Set<string>();
  const compiledPrimitives = new Map<string, Record<string, unknown>>();

  for (const feature of input.recipe.features) {
    if (ids.has(feature.id)) throw new Error(`Duplicate feature id: ${feature.id}`);
    ids.add(feature.id);
    if (feature.type === "boolean") {
      const dependencies = [feature.target, ...feature.tools];
      if (new Set(dependencies).size !== dependencies.length) {
        throw new Error(`Feature ${feature.id} must use distinct target and tool features`);
      }
      for (const dependency of dependencies) {
        if (!available.has(dependency)) {
          throw new Error(`Feature ${feature.id} references missing or consumed feature: ${dependency}`);
        }
      }
      available.delete(feature.target);
      for (const tool of feature.tools) available.delete(tool);
    } else {
      compiledPrimitives.set(feature.id, primitiveEntity(feature, parameters));
    }
    available.add(feature.id);
  }

  try {
    let primitiveBatch: Array<{
      feature: Exclude<z.infer<typeof featureSchema>, { type: "boolean" }>;
      entity: Record<string, unknown>;
    }> = [];

    const flushPrimitives = async (): Promise<void> => {
      if (primitiveBatch.length === 0) return;
      const batch = primitiveBatch;
      primitiveBatch = [];
      const response = await bridge.call<CreatedResponse>("solid", {
        action: "create",
        primitives: batch.map((item) => item.entity),
      });
      const handles = requireCreatedHandles(response, batch.length, "Primitive batch");
      batch.forEach(({ feature }, index) => {
        const handle = handles[index];
        featureHandles[feature.id] = handle;
        liveByFeature.set(feature.id, handle);
        trace.push({ featureId: feature.id, type: feature.type, handle });
      });
    };

    for (const feature of input.recipe.features) {
      if (feature.type === "boolean") {
        await flushPrimitives();
        const targetHandle = liveByFeature.get(feature.target)!;
        const toolHandles = feature.tools.map((id) => liveByFeature.get(id)!);
        const response = await bridge.call<BooleanResponse>("solid", {
          action: "boolean",
          operation: feature.operation,
          targetHandle,
          toolHandles,
        });
        const handle = requireBooleanHandle(response, `Boolean feature ${feature.id}`);
        liveByFeature.delete(feature.target);
        for (const tool of feature.tools) liveByFeature.delete(tool);
        featureHandles[feature.id] = handle;
        liveByFeature.set(feature.id, handle);
        trace.push({ featureId: feature.id, type: feature.type, handle });
      } else {
        primitiveBatch.push({ feature, entity: compiledPrimitives.get(feature.id)! });
        if (primitiveBatch.length === 250) await flushPrimitives();
      }
    }
    await flushPrimitives();
  } catch (error) {
    try {
      await eraseHandles(bridge, liveByFeature.values());
    } catch {
      // Preserve the original build error; best-effort compensation is sufficient here.
    }
    throw error;
  }

  const warnings: string[] = [];
  try {
    await eraseHandles(bridge, input.previousHandles ?? []);
  } catch (error) {
    warnings.push(`Model rebuilt, but previous entities could not be removed: ${String(error)}`);
  }

  return {
    modelId: input.recipe.id,
    parameters,
    featureHandles,
    liveHandles: [...new Set(liveByFeature.values())],
    trace,
    warnings,
  };
}
