import { z } from "zod";

const point2 = z.tuple([z.number(), z.number()]);
const point3 = z.tuple([z.number(), z.number(), z.number()]);
const point = z.union([point2, point3]);
const entityFilter = z.object({
  types: z.array(z.string()).optional(),
  layer: z.string().optional(),
  colorIndex: z.number().int().min(0).max(256).optional(),
  textContains: z.string().optional(),
  window: z.tuple([point, point]).optional(),
});
const target = z.object({
  handles: z.array(z.string()).optional().describe("Exact AutoCAD entity handles"),
  filter: entityFilter.optional(),
}).refine((value) => value.handles?.length || value.filter, "Provide handles or a filter");

const commonEntity = {
  layer: z.string().optional(),
  colorIndex: z.number().int().min(0).max(256).optional(),
  linetype: z.string().optional(),
};

const entity = z.discriminatedUnion("type", [
  z.object({ type: z.literal("line"), start: point, end: point, ...commonEntity }),
  z.object({ type: z.literal("polyline"), points: z.array(point2).min(2), closed: z.boolean().optional(), width: z.number().nonnegative().optional(), ...commonEntity }),
  z.object({ type: z.literal("circle"), center: point, radius: z.number().positive(), ...commonEntity }),
  z.object({ type: z.literal("arc"), center: point, radius: z.number().positive(), startAngle: z.number(), endAngle: z.number(), ...commonEntity }),
  z.object({ type: z.literal("ellipse"), center: point, majorAxis: point3, radiusRatio: z.number().positive().max(1), startParameter: z.number().optional(), endParameter: z.number().optional(), ...commonEntity }),
  z.object({ type: z.literal("point"), position: point, ...commonEntity }),
  z.object({ type: z.literal("text"), position: point, text: z.string(), height: z.number().positive(), rotation: z.number().optional(), style: z.string().optional(), ...commonEntity }),
  z.object({ type: z.literal("mtext"), position: point, text: z.string(), height: z.number().positive(), width: z.number().positive(), rotation: z.number().optional(), style: z.string().optional(), ...commonEntity }),
  z.object({ type: z.literal("hatch"), pattern: z.string().default("SOLID"), scale: z.number().positive().optional(), angle: z.number().optional(), boundaryHandles: z.array(z.string()).min(1), associative: z.boolean().optional(), ...commonEntity }),
  z.object({ type: z.literal("blockReference"), name: z.string(), insertionPoint: point, scale: point3.optional(), rotation: z.number().optional(), attributes: z.record(z.string()).optional(), ...commonEntity }),
  z.object({ type: z.literal("alignedDimension"), first: point, second: point, dimensionLine: point, style: z.string().optional(), ...commonEntity }),
  z.object({ type: z.literal("rotatedDimension"), first: point, second: point, dimensionLine: point, rotation: z.number(), style: z.string().optional(), ...commonEntity }),
]);

const editOperation = z.discriminatedUnion("op", [
  z.object({ op: z.literal("setProperties"), layer: z.string().optional(), colorIndex: z.number().int().min(0).max(256).optional(), linetype: z.string().optional(), lineweight: z.number().optional() }),
  z.object({ op: z.literal("move"), from: point, to: point }),
  z.object({ op: z.literal("copy"), from: point, to: point }),
  z.object({ op: z.literal("rotate"), base: point, angle: z.number() }),
  z.object({ op: z.literal("scale"), base: point, factor: z.number().positive() }),
  z.object({ op: z.literal("mirror"), first: point, second: point, eraseSource: z.boolean().optional() }),
  z.object({ op: z.literal("erase") }),
]);

const solidPrimitive = z.discriminatedUnion("type", [
  z.object({ type: z.literal("box"), origin: point, length: z.number().positive(), width: z.number().positive(), height: z.number().positive(), layer: z.string().optional(), colorIndex: z.number().int().min(0).max(256).optional() }),
  z.object({ type: z.literal("sphere"), center: point, radius: z.number().positive(), layer: z.string().optional(), colorIndex: z.number().int().min(0).max(256).optional() }),
  z.object({ type: z.literal("cylinder"), center: point, radius: z.number().positive(), height: z.number().positive(), axis: point3.optional(), layer: z.string().optional(), colorIndex: z.number().int().min(0).max(256).optional() }),
  z.object({ type: z.literal("cone"), center: point, baseRadius: z.number().positive(), topRadius: z.number().nonnegative(), height: z.number().positive(), axis: point3.optional(), layer: z.string().optional(), colorIndex: z.number().int().min(0).max(256).optional() }),
]);

export type ToolDefinition = {
  name: string;
  description: string;
  schema: z.ZodRawShape;
  method: string;
  readOnly?: boolean;
};

export const toolDefinitions: ToolDefinition[] = [
  {
    name: "cad_instances",
    description: "List running AutoCAD 2024 processes registered with this MCP server, including PID and active drawing. Use this before cad_connect when multiple AutoCAD windows are open.",
    schema: {}, method: "__local_instances", readOnly: true,
  },
  {
    name: "cad_connect",
    description: "Bind subsequent MCP calls to one specific AutoCAD process. This prevents operations from being routed to the wrong drawing when multiple instances are running.",
    schema: { processId: z.number().int().positive() }, method: "__local_connect",
  },
  {
    name: "cad_status",
    description: "Check the AutoCAD 2024 bridge, active document, product version, and current drawing state.",
    schema: {}, method: "status", readOnly: true,
  },
  {
    name: "cad_capabilities",
    description: "Return the exact 0.0.3 feature matrix, limits, coordinate conventions, and verified export behavior without contacting AutoCAD.",
    schema: {}, method: "__local_capabilities", readOnly: true,
  },
  {
    name: "cad_document",
    description: "Create, open, save, save-as, close, activate, or list AutoCAD drawings. Paths must be absolute local paths.",
    schema: {
      action: z.enum(["list", "new", "open", "save", "saveAs", "saveCopy", "close", "activate"]),
      path: z.string().optional(), template: z.string().optional(), documentId: z.string().optional(),
      readOnly: z.boolean().optional(), discardChanges: z.boolean().optional(), overwrite: z.boolean().optional(),
      version: z.enum(["2018", "2013", "2010", "2007", "2004", "2000"]).optional(),
    }, method: "document",
  },
  {
    name: "cad_inspect",
    description: "Read drawing metadata, system variables, layers, blocks, layouts, styles, or entities. Use handles returned here for precise edits.",
    schema: {
      scope: z.enum(["drawing", "entities", "layers", "blocks", "layouts", "materials", "textStyles", "dimensionStyles", "linetypes", "systemVariables"]),
      handles: z.array(z.string()).optional(),
      filter: entityFilter.optional(),
      names: z.array(z.string()).optional().describe("System variable names or named-object names"),
      includeGeometry: z.boolean().optional(), includeExtents: z.boolean().optional(),
      offset: z.number().int().nonnegative().optional(), limit: z.number().int().min(1).max(5000).optional(),
    }, method: "inspect", readOnly: true,
  },
  {
    name: "cad_solid",
    description: "Create editable AutoCAD 3DSOLID primitives or apply boolean union, subtract, and intersect operations. Coordinates are WCS and one request is atomic.",
    schema: {
      action: z.enum(["create", "boolean"]),
      primitives: z.array(solidPrimitive).min(1).max(250).optional(),
      operation: z.enum(["union", "subtract", "intersect"]).optional(),
      targetHandle: z.string().optional(),
      toolHandles: z.array(z.string()).min(1).optional(),
      eraseTools: z.boolean().optional(),
    }, method: "solid",
  },
  {
    name: "cad_measure",
    description: "Measure selected entities: extents, curve length, planar area, 3D solid volume, centroid, and optional mass from density.",
    schema: {
      handles: z.array(z.string()).min(1).max(500),
      density: z.number().positive().optional().describe("Mass density in drawing mass-units per cubic drawing-unit"),
    }, method: "measure", readOnly: true,
  },
  {
    name: "cad_draw",
    description: "Atomically create 2D entities, annotations, hatches, dimensions, and block references. Angles are radians and coordinates use the current UCS unless otherwise stated.",
    schema: {
      entities: z.array(entity).min(1).max(1000),
      space: z.enum(["model", "paper", "current"]).optional(),
      coordinateSystem: z.enum(["UCS", "WCS"]).optional(),
      zoomToResult: z.boolean().optional(),
    }, method: "draw",
  },
  {
    name: "cad_edit",
    description: "Atomically edit entities selected by exact handles or a constrained filter. Supports properties, move, copy, rotate, scale, mirror, and erase.",
    schema: { target, operations: z.array(editOperation).min(1).max(50), allowEmpty: z.boolean().optional() }, method: "edit",
  },
  {
    name: "cad_layer",
    description: "Create, update, delete, rename, set current, isolate, or restore drawing layers.",
    schema: {
      action: z.enum(["create", "update", "delete", "rename", "setCurrent", "isolate", "restore"]),
      name: z.string().optional(), newName: z.string().optional(),
      properties: z.object({ colorIndex: z.number().int().min(1).max(255).optional(), linetype: z.string().optional(), lineweight: z.number().optional(), on: z.boolean().optional(), frozen: z.boolean().optional(), locked: z.boolean().optional(), plottable: z.boolean().optional(), description: z.string().optional() }).optional(),
    }, method: "layer",
  },
  {
    name: "cad_block",
    description: "Create a block definition from entities, insert a block, redefine it, or import a DWG as a block.",
    schema: {
      action: z.enum(["create", "insert", "import"]), name: z.string(),
      sourceHandles: z.array(z.string()).optional(), sourcePath: z.string().optional(), basePoint: point.optional(),
      insertionPoint: point.optional(), scale: point3.optional(), rotation: z.number().optional(),
      attributes: z.record(z.string()).optional(), deleteSource: z.boolean().optional(),
    }, method: "block",
  },
  {
    name: "cad_layout",
    description: "Create, rename, delete, activate, configure, or list paper-space layouts and their plot settings.",
    schema: {
      action: z.enum(["list", "create", "rename", "delete", "activate", "configure"]),
      name: z.string().optional(), newName: z.string().optional(),
      settings: z.object({ device: z.string().optional(), media: z.string().optional(), styleSheet: z.string().optional(), orientation: z.enum(["portrait", "landscape"]).optional(), plotType: z.enum(["layout", "extents", "window", "display"]).optional(), window: z.tuple([point2, point2]).optional(), scaleToFit: z.boolean().optional(), scale: z.number().positive().optional(), centered: z.boolean().optional() }).optional(),
    }, method: "layout",
  },
  {
    name: "cad_export",
    description: "Export the active drawing or a layout to DWG, DXF, PDF, DWF, SVG, PNG, or JPEG. The destination must be an absolute path.",
    schema: {
      format: z.enum(["dwg", "dxf", "stl", "pdf", "dwf", "svg", "png", "jpeg"]), path: z.string(),
      layout: z.string().optional(), selection: z.array(z.string()).optional(),
      overwrite: z.boolean().optional(), version: z.enum(["2018", "2013", "2010", "2007", "2004", "2000"]).optional(),
      options: z.record(z.union([z.string(), z.number(), z.boolean()])).optional(),
    }, method: "export",
  },
  {
    name: "cad_view",
    description: "Zoom, pan, orbit, switch named views, regenerate, or capture a preview image.",
    schema: {
      action: z.enum(["zoomExtents", "zoomWindow", "zoomObjects", "pan", "orbit", "setView", "regen", "capture"]),
      window: z.tuple([point, point]).optional(), handles: z.array(z.string()).optional(),
      delta: point2.optional(), viewName: z.string().optional(), path: z.string().optional(), width: z.number().int().positive().optional(), height: z.number().int().positive().optional(),
    }, method: "view",
  },
  {
    name: "cad_undo",
    description: "Undo or redo completed MCP edit operations in the active drawing.",
    schema: { action: z.enum(["undo", "redo"]), steps: z.number().int().min(1).max(20).optional() }, method: "undo",
  },
  {
    name: "cad_command",
    description: "Run an AutoCAD command sequence only when a specialized MCP tool cannot express the operation. Commands are blocked unless explicitly allowlisted in the plugin configuration.",
    schema: { command: z.string().min(1), arguments: z.array(z.union([z.string(), z.number()])).optional(), waitForCompletion: z.boolean().optional() }, method: "command",
  },
];
