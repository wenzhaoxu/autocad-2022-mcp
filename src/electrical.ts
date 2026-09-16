import { z } from "zod";

type Point2 = [number, number];
type SymbolPrimitive =
  | { kind: "line"; start: Point2; end: Point2 }
  | { kind: "polyline"; points: Point2[]; closed?: boolean }
  | { kind: "circle"; center: Point2; radius: number }
  | { kind: "arc"; center: Point2; radius: number; startAngle: number; endAngle: number }
  | { kind: "text"; position: Point2; text: string; height: number; rotation?: number };

export type ElectricalSymbol = {
  id: string;
  nameZh: string;
  nameEn: string;
  category: string;
  aliases: string[];
  description: string;
  terminals: Point2[];
  primitives: SymbolPrimitive[];
};

const line = (start: Point2, end: Point2): SymbolPrimitive => ({ kind: "line", start, end });
const circle = (center: Point2, radius: number): SymbolPrimitive => ({ kind: "circle", center, radius });
const text = (position: Point2, value: string, height = 4): SymbolPrimitive => ({ kind: "text", position, text: value, height });

const SYMBOLS: ElectricalSymbol[] = [
  {
    id: "resistor", nameZh: "电阻器", nameEn: "Resistor", category: "passive", aliases: ["电阻", "R"],
    description: "IEC 矩形表达的固定电阻器。", terminals: [[-10, 0], [10, 0]],
    primitives: [line([-10, 0], [-6, 0]), { kind: "polyline", points: [[-6, -3], [6, -3], [6, 3], [-6, 3]], closed: true }, line([6, 0], [10, 0])],
  },
  {
    id: "variable-resistor", nameZh: "可变电阻器", nameEn: "Variable resistor", category: "passive", aliases: ["变阻器", "电位器"],
    description: "带斜向调节箭头的可变电阻器。", terminals: [[-10, 0], [10, 0]],
    primitives: [line([-10, 0], [-6, 0]), { kind: "polyline", points: [[-6, -3], [6, -3], [6, 3], [-6, 3]], closed: true }, line([6, 0], [10, 0]), line([-4, -6], [4, 6]), line([4, 6], [1.5, 4.8]), line([4, 6], [3.6, 3.3])],
  },
  {
    id: "capacitor", nameZh: "电容器", nameEn: "Capacitor", category: "passive", aliases: ["电容", "C"],
    description: "无极性电容器。", terminals: [[-10, 0], [10, 0]],
    primitives: [line([-10, 0], [-2, 0]), line([-2, -5], [-2, 5]), line([2, -5], [2, 5]), line([2, 0], [10, 0])],
  },
  {
    id: "polarized-capacitor", nameZh: "有极性电容器", nameEn: "Polarized capacitor", category: "passive", aliases: ["电解电容"],
    description: "标有正极的有极性电容器。", terminals: [[-10, 0], [10, 0]],
    primitives: [line([-10, 0], [-2, 0]), line([-2, -5], [-2, 5]), line([2, -5], [2, 5]), line([2, 0], [10, 0]), text([-5, 4], "+", 3)],
  },
  {
    id: "inductor", nameZh: "电感器", nameEn: "Inductor", category: "passive", aliases: ["线圈", "电抗器"],
    description: "四段线圈表达的电感器。", terminals: [[-10, 0], [10, 0]],
    primitives: [line([-10, 0], [-8, 0]), ...[-6, -2, 2, 6].map((x) => ({ kind: "arc", center: [x, 0] as Point2, radius: 2, startAngle: 0, endAngle: Math.PI } as SymbolPrimitive)), line([8, 0], [10, 0])],
  },
  {
    id: "transformer", nameZh: "变压器", nameEn: "Transformer", category: "power", aliases: ["T", "电源变压器"],
    description: "双绕组、带铁芯的变压器。", terminals: [[-10, 6], [-10, -6], [10, 6], [10, -6]],
    primitives: [line([-10, 6], [-6, 6]), line([-10, -6], [-6, -6]), line([10, 6], [6, 6]), line([10, -6], [6, -6]), ...[-4, 0, 4].map((y) => ({ kind: "arc", center: [-6, y] as Point2, radius: 2, startAngle: -Math.PI / 2, endAngle: Math.PI / 2 } as SymbolPrimitive)), ...[-4, 0, 4].map((y) => ({ kind: "arc", center: [6, y] as Point2, radius: 2, startAngle: Math.PI / 2, endAngle: Math.PI * 1.5 } as SymbolPrimitive)), line([-1, -7], [-1, 7]), line([1, -7], [1, 7])],
  },
  {
    id: "battery", nameZh: "直流电池", nameEn: "Battery", category: "source", aliases: ["电池", "直流电源"],
    description: "长短线组表示的直流电池。", terminals: [[-10, 0], [10, 0]],
    primitives: [line([-10, 0], [-5, 0]), line([-5, -4], [-5, 4]), line([-1.5, -2.5], [-1.5, 2.5]), line([1.5, -4], [1.5, 4]), line([5, -2.5], [5, 2.5]), line([5, 0], [10, 0]), text([-7, 4], "+", 3), text([5, 3], "-", 3)],
  },
  {
    id: "ac-source", nameZh: "交流电源", nameEn: "AC source", category: "source", aliases: ["交流源", "AC"],
    description: "圆内正弦标识的交流电源。", terminals: [[-10, 0], [10, 0]],
    primitives: [line([-10, 0], [-5, 0]), circle([0, 0], 5), { kind: "polyline", points: [[-3.2, 0], [-1.6, 1.8], [0, 0], [1.6, -1.8], [3.2, 0]] }, line([5, 0], [10, 0])],
  },
  {
    id: "earth", nameZh: "接地", nameEn: "Earth", category: "connection", aliases: ["功能接地", "地"],
    description: "一般接地符号。", terminals: [[0, 8]],
    primitives: [line([0, 8], [0, 0]), line([-5, 0], [5, 0]), line([-3.5, -2], [3.5, -2]), line([-2, -4], [2, -4])],
  },
  {
    id: "protective-earth", nameZh: "保护接地", nameEn: "Protective earth", category: "connection", aliases: ["PE", "保护地"],
    description: "带圆环的保护接地符号。", terminals: [[0, 9]],
    primitives: [circle([0, 0], 7), line([0, 9], [0, 3]), line([-4.5, 3], [4.5, 3]), line([-3, 0], [3, 0]), line([-1.5, -3], [1.5, -3])],
  },
  {
    id: "switch-no", nameZh: "常开开关", nameEn: "Normally open switch", category: "switching", aliases: ["常开触点", "NO"],
    description: "静态位置断开的开关触点。", terminals: [[-10, 0], [10, 0]],
    primitives: [line([-10, 0], [-4, 0]), circle([-3, 0], 1), circle([3, 0], 1), line([-2.5, 1], [2.5, 5]), line([4, 0], [10, 0])],
  },
  {
    id: "switch-nc", nameZh: "常闭开关", nameEn: "Normally closed switch", category: "switching", aliases: ["常闭触点", "NC"],
    description: "静态位置闭合的开关触点。", terminals: [[-10, 0], [10, 0]],
    primitives: [line([-10, 0], [-4, 0]), circle([-3, 0], 1), circle([3, 0], 1), line([-2, 0], [2, 0]), line([4, 0], [10, 0])],
  },
  {
    id: "pushbutton-no", nameZh: "常开按钮", nameEn: "Normally open pushbutton", category: "switching", aliases: ["启动按钮", "SB-NO"],
    description: "具有机械操作杆的常开按钮。", terminals: [[-10, 0], [10, 0]],
    primitives: [line([-10, 0], [-4, 0]), circle([-3, 0], 1), circle([3, 0], 1), line([4, 0], [10, 0]), line([-2, 4], [2, 4]), line([0, 4], [0, 1])],
  },
  {
    id: "pushbutton-nc", nameZh: "常闭按钮", nameEn: "Normally closed pushbutton", category: "switching", aliases: ["停止按钮", "SB-NC"],
    description: "具有机械操作杆的常闭按钮。", terminals: [[-10, 0], [10, 0]],
    primitives: [line([-10, 0], [-4, 0]), circle([-3, 0], 1), circle([3, 0], 1), line([-2, 0], [2, 0]), line([4, 0], [10, 0]), line([-2, 4], [2, 4]), line([0, 4], [0, 1])],
  },
  {
    id: "fuse", nameZh: "熔断器", nameEn: "Fuse", category: "protection", aliases: ["FU", "保险丝"],
    description: "矩形内熔体线表示的熔断器。", terminals: [[-10, 0], [10, 0]],
    primitives: [line([-10, 0], [-6, 0]), { kind: "polyline", points: [[-6, -2.5], [6, -2.5], [6, 2.5], [-6, 2.5]], closed: true }, line([-6, -2.5], [6, 2.5]), line([6, 0], [10, 0])],
  },
  {
    id: "circuit-breaker", nameZh: "断路器", nameEn: "Circuit breaker", category: "protection", aliases: ["QF", "自动开关"],
    description: "带自动脱扣标识的断路器。", terminals: [[-10, 0], [10, 0]],
    primitives: [line([-10, 0], [-4, 0]), circle([-3, 0], 1), circle([3, 0], 1), line([-2.5, 1], [2.5, 5]), line([4, 0], [10, 0]), { kind: "arc", center: [0, 3], radius: 2.5, startAngle: Math.PI, endAngle: Math.PI * 2 }],
  },
  {
    id: "relay-coil", nameZh: "继电器线圈", nameEn: "Relay coil", category: "control", aliases: ["接触器线圈", "KM", "KA"],
    description: "矩形表达的继电器或接触器线圈。", terminals: [[-10, 0], [10, 0]],
    primitives: [line([-10, 0], [-6, 0]), { kind: "polyline", points: [[-6, -4], [6, -4], [6, 4], [-6, 4]], closed: true }, line([6, 0], [10, 0]), text([-3, -1.8], "K", 4)],
  },
  {
    id: "indicator-lamp", nameZh: "指示灯", nameEn: "Indicator lamp", category: "control", aliases: ["信号灯", "HL"],
    description: "圆内交叉线表示的指示灯。", terminals: [[-10, 0], [10, 0]],
    primitives: [line([-10, 0], [-5, 0]), circle([0, 0], 5), line([-3.5, -3.5], [3.5, 3.5]), line([-3.5, 3.5], [3.5, -3.5]), line([5, 0], [10, 0])],
  },
  {
    id: "buzzer", nameZh: "蜂鸣器", nameEn: "Buzzer", category: "control", aliases: ["电笛", "HA"],
    description: "电声报警器符号。", terminals: [[-10, 0], [10, 0]],
    primitives: [line([-10, 0], [-5, 0]), { kind: "polyline", points: [[-5, -3], [0, -3], [4, -6], [4, 6], [0, 3], [-5, 3]], closed: true }, line([4, 0], [10, 0]), { kind: "arc", center: [5, 0], radius: 3, startAngle: -Math.PI / 3, endAngle: Math.PI / 3 }],
  },
  {
    id: "motor-3phase", nameZh: "三相电动机", nameEn: "Three-phase motor", category: "machine", aliases: ["电动机", "M", "三相异步电机"],
    description: "三相交流电动机。", terminals: [[-10, 0], [10, 0]],
    primitives: [line([-10, 0], [-7, 0]), circle([0, 0], 7), text([-2.8, -2], "M", 4.5), text([1.5, 1.5], "3~", 2.5), line([7, 0], [10, 0])],
  },
  {
    id: "ammeter", nameZh: "电流表", nameEn: "Ammeter", category: "meter", aliases: ["A表", "PA"],
    description: "电流测量仪表。", terminals: [[-10, 0], [10, 0]],
    primitives: [line([-10, 0], [-6, 0]), circle([0, 0], 6), text([-2, -2], "A", 4), line([6, 0], [10, 0])],
  },
  {
    id: "voltmeter", nameZh: "电压表", nameEn: "Voltmeter", category: "meter", aliases: ["V表", "PV"],
    description: "电压测量仪表。", terminals: [[-10, 0], [10, 0]],
    primitives: [line([-10, 0], [-6, 0]), circle([0, 0], 6), text([-2, -2], "V", 4), line([6, 0], [10, 0])],
  },
  {
    id: "diode", nameZh: "二极管", nameEn: "Diode", category: "semiconductor", aliases: ["整流二极管", "VD"],
    description: "普通半导体二极管。", terminals: [[-10, 0], [10, 0]],
    primitives: [line([-10, 0], [-5, 0]), { kind: "polyline", points: [[-5, -4], [3, 0], [-5, 4]], closed: true }, line([3, -5], [3, 5]), line([3, 0], [10, 0])],
  },
  {
    id: "led", nameZh: "发光二极管", nameEn: "Light-emitting diode", category: "semiconductor", aliases: ["LED", "发光管"],
    description: "带向外光线箭头的发光二极管。", terminals: [[-10, 0], [10, 0]],
    primitives: [line([-10, 0], [-5, 0]), { kind: "polyline", points: [[-5, -4], [3, 0], [-5, 4]], closed: true }, line([3, -5], [3, 5]), line([3, 0], [10, 0]), line([-1, 5], [3, 9]), line([3, 9], [1, 8.5]), line([3, 9], [2.5, 7]), line([3, 5], [7, 9]), line([7, 9], [5, 8.5]), line([7, 9], [6.5, 7])],
  },
  {
    id: "terminal", nameZh: "接线端子", nameEn: "Terminal", category: "connection", aliases: ["端子", "XT"],
    description: "可编号的接线端子基本符号。", terminals: [[-10, 0], [10, 0]],
    primitives: [line([-10, 0], [-2, 0]), circle([0, 0], 2), line([2, 0], [10, 0])],
  },
  {
    id: "junction", nameZh: "导线连接点", nameEn: "Connected junction", category: "connection", aliases: ["连接点", "节点"],
    description: "带实心连接点的导线交汇。", terminals: [[-8, 0], [8, 0], [0, 8], [0, -8]],
    primitives: [line([-8, 0], [8, 0]), line([0, -8], [0, 8]), circle([0, 0], 1.2)],
  },
  {
    id: "crossing-no-connection", nameZh: "导线交叉不连接", nameEn: "Crossing without connection", category: "connection", aliases: ["跨线", "不连接交叉"],
    description: "一条导线跨越另一条且不形成电气连接。", terminals: [[-8, 0], [8, 0], [0, 8], [0, -8]],
    primitives: [line([0, -8], [0, 8]), line([-8, 0], [-2.5, 0]), { kind: "arc", center: [0, 0], radius: 2.5, startAngle: 0, endAngle: Math.PI }, line([2.5, 0], [8, 0])],
  },
];

export const ELECTRICAL_SYMBOLS = Object.freeze(SYMBOLS);
export const ELECTRICAL_SYMBOL_IDS = SYMBOLS.map((symbol) => symbol.id) as [string, ...string[]];

export const ELECTRICAL_DRAWING_SHEET_STANDARD = `# 电气图样幅面、图框与标题栏基线

## 图面构成

电气图面由纸张边框线、图框线、标题栏和会签栏组成。边框线所围成的图面定义图纸幅面。标题栏通常位于图纸右下方或下方，文字方向与看图方向一致；会签栏供相关专业设计人员签名并填写日期。

## 基本幅面

| 幅面 | 长边（mm） | 短边（mm） |
|---|---:|---:|
| A0 | 1189 | 841 |
| A1 | 841 | 594 |
| A2 | 594 | 420 |
| A3 | 420 | 297 |
| A4 | 297 | 210 |

A0 至 A2 一般不加长；A3、A4 可按项目制图标准沿短边方向采用加长幅面。MCP 以短边为模数增加长边，即长边增加 \`extensionModules × 短边\`。选择幅面时应兼顾对象规模、图样详细程度、紧凑清晰、装订管理、复制缩微及计算机辅助设计要求，并尽量采用能够清晰表达内容的较小幅面。

## 标题栏、会签栏和编号

标题栏至少表达图样名称、图号、张次、更改标记、日期及设计、校核、审核等责任信息。图样编号由图号和检索号两部分组成。会签栏记录相关专业、签名和日期。

## 图幅分区

图框竖边方向用大写拉丁字母，横边方向用阿拉伯数字。编号顺序从与标题栏相对的左上角开始，横向和纵向分区数量均应为偶数；单个分区代号采用“字母+数字”，例如 A1。

## MCP 实现说明

调用 \`cad_electrical_sheet\` 可生成 A0 至 A4 外框、内图框、标题栏、会签栏及分区标识。默认装订边为左侧 25 mm；其他默认边距在 A0 至 A2 为 10 mm、A3 至 A4 为 5 mm。这些边距是可覆盖的工程默认值，正式出图前应以项目采用的国家、行业或企业标准复核。
`;

export const ELECTRICAL_SYMBOL_CATALOG = {
  resourceVersion: "1.0",
  coordinateConvention: "Normalized 2D geometry; insertion point is the symbol origin; angles are radians.",
  standardAlignment: "Generic IEC 60617 / GB/T 4728-aligned schematic semantics; verify the required project edition before issue.",
  symbols: SYMBOLS.map(({ primitives: _primitives, ...summary }) => summary),
};

export function getElectricalSymbol(id: string): ElectricalSymbol | undefined {
  return SYMBOLS.find((symbol) => symbol.id === id);
}

const point2Schema = z.tuple([z.number(), z.number()]);
const presentationSchema = {
  expectedDocumentId: z.string().min(1).optional(),
  expectedRevision: z.number().int().nonnegative().optional(),
  visualMode: z.enum(["silent", "batch", "step"]).optional(),
  highlightResults: z.boolean().optional(),
  zoomToResult: z.boolean().optional(),
};

export const electricalSymbolInputSchema = z.object({
  symbolId: z.enum(ELECTRICAL_SYMBOL_IDS),
  insertionPoint: point2Schema,
  scale: z.number().positive().default(1),
  rotation: z.number().finite().default(0),
  layer: z.string().min(1).optional(),
  colorIndex: z.number().int().min(0).max(256).optional(),
  tag: z.string().max(100).optional(),
  textHeight: z.number().positive().optional(),
  textStyle: z.string().min(1).optional(),
  space: z.enum(["model", "paper", "current"]).default("current"),
  coordinateSystem: z.enum(["UCS", "WCS"]).default("UCS"),
  ...presentationSchema,
}).strict();

const titleBlockSchema = z.object({
  project: z.string().max(200).optional(),
  drawingName: z.string().max(200).optional(),
  drawingNumber: z.string().max(100).optional(),
  retrievalNumber: z.string().max(100).optional(),
  sheetIndex: z.string().max(20).optional(),
  sheetCount: z.string().max(20).optional(),
  revision: z.string().max(40).optional(),
  date: z.string().max(40).optional(),
  designedBy: z.string().max(80).optional(),
  checkedBy: z.string().max(80).optional(),
  approvedBy: z.string().max(80).optional(),
  organization: z.string().max(200).optional(),
}).strict();

export const electricalSheetInputSchema = z.object({
  sheet: z.enum(["A0", "A1", "A2", "A3", "A4"]),
  orientation: z.enum(["landscape", "portrait"]).optional(),
  extensionModules: z.number().int().min(0).max(6).default(0).describe("A3/A4 only: add this many short-edge modules to the long edge"),
  origin: point2Schema.default([0, 0]),
  scale: z.number().positive().default(1),
  binding: z.boolean().default(true),
  margins: z.object({
    left: z.number().nonnegative().optional(),
    right: z.number().nonnegative().optional(),
    top: z.number().nonnegative().optional(),
    bottom: z.number().nonnegative().optional(),
  }).strict().optional(),
  zoneColumns: z.number().int().min(2).max(26).refine((value) => value % 2 === 0, "zoneColumns must be even").optional(),
  zoneRows: z.number().int().min(2).max(26).refine((value) => value % 2 === 0, "zoneRows must be even").optional(),
  titleBlock: titleBlockSchema.optional(),
  cosigners: z.array(z.object({
    discipline: z.string().max(40),
    name: z.string().max(40),
    date: z.string().max(40),
  }).strict()).max(5).default([]),
  textHeight: z.number().positive().default(2.5),
  textStyle: z.string().min(1).optional(),
  layer: z.string().min(1).optional(),
  colorIndex: z.number().int().min(0).max(256).optional(),
  space: z.enum(["model", "paper", "current"]).default("current"),
  coordinateSystem: z.enum(["UCS", "WCS"]).default("UCS"),
  ...presentationSchema,
}).strict();

type DrawEntity = Record<string, unknown>;
type ElectricalBridge = {
  call(method: string, params: unknown, options?: { signal?: AbortSignal }): Promise<unknown>;
};

function transform(point: Point2, origin: Point2, scale: number, rotation: number): Point2 {
  const x = point[0] * scale;
  const y = point[1] * scale;
  const cosine = Math.cos(rotation);
  const sine = Math.sin(rotation);
  return [origin[0] + x * cosine - y * sine, origin[1] + x * sine + y * cosine];
}

function common(layer?: string, colorIndex?: number): Record<string, unknown> {
  return { ...(layer ? { layer } : {}), ...(colorIndex !== undefined ? { colorIndex } : {}) };
}

export function buildElectricalSymbolEntities(raw: unknown): {
  input: z.infer<typeof electricalSymbolInputSchema>;
  symbol: ElectricalSymbol;
  entities: DrawEntity[];
} {
  const input = electricalSymbolInputSchema.parse(raw);
  const symbol = getElectricalSymbol(input.symbolId);
  if (!symbol) throw new Error(`Unknown electrical symbol: ${input.symbolId}`);
  const shared = common(input.layer, input.colorIndex);
  const entities: DrawEntity[] = symbol.primitives.map((primitive) => {
    if (primitive.kind === "line") return { type: "line", start: transform(primitive.start, input.insertionPoint, input.scale, input.rotation), end: transform(primitive.end, input.insertionPoint, input.scale, input.rotation), ...shared };
    if (primitive.kind === "polyline") return { type: "polyline", points: primitive.points.map((point) => transform(point, input.insertionPoint, input.scale, input.rotation)), closed: primitive.closed, ...shared };
    if (primitive.kind === "circle") return { type: "circle", center: transform(primitive.center, input.insertionPoint, input.scale, input.rotation), radius: primitive.radius * input.scale, ...shared };
    if (primitive.kind === "arc") return { type: "arc", center: transform(primitive.center, input.insertionPoint, input.scale, input.rotation), radius: primitive.radius * input.scale, startAngle: primitive.startAngle + input.rotation, endAngle: primitive.endAngle + input.rotation, ...shared };
    return { type: "text", position: transform(primitive.position, input.insertionPoint, input.scale, input.rotation), text: primitive.text, height: primitive.height * input.scale, rotation: (primitive.rotation ?? 0) + input.rotation, ...(input.textStyle ? { style: input.textStyle } : {}), ...shared };
  });
  if (input.tag) {
    entities.push({
      type: "text",
      position: transform([0, 9], input.insertionPoint, input.scale, input.rotation),
      text: input.tag,
      height: input.textHeight ?? Math.max(2.5, 3 * input.scale),
      rotation: input.rotation,
      ...(input.textStyle ? { style: input.textStyle } : {}),
      ...shared,
    });
  }
  return { input, symbol, entities };
}

export async function drawElectricalSymbol(bridge: ElectricalBridge, raw: unknown, signal?: AbortSignal): Promise<unknown> {
  const { input, symbol, entities } = buildElectricalSymbolEntities(raw);
  const result = await bridge.call("draw", {
    entities,
    space: input.space,
    coordinateSystem: input.coordinateSystem,
    expectedDocumentId: input.expectedDocumentId,
    expectedRevision: input.expectedRevision,
    visualMode: input.visualMode,
    highlightResults: input.highlightResults,
    zoomToResult: input.zoomToResult,
  }, { signal });
  return { symbolId: symbol.id, name: symbol.nameZh, entityCount: entities.length, result };
}

const SHEET_SIZES: Record<string, Point2> = {
  A0: [1189, 841], A1: [841, 594], A2: [594, 420], A3: [420, 297], A4: [297, 210],
};
const DEFAULT_ZONES: Record<string, Point2> = {
  A0: [16, 12], A1: [12, 8], A2: [10, 8], A3: [8, 6], A4: [6, 4],
};

function rectangle(left: number, bottom: number, right: number, top: number, shared: Record<string, unknown>): DrawEntity {
  return { type: "polyline", points: [[left, bottom], [right, bottom], [right, top], [left, top]], closed: true, ...shared };
}

function sheetText(position: Point2, value: string, height: number, style: string | undefined, shared: Record<string, unknown>): DrawEntity {
  return { type: "text", position, text: value, height, ...(style ? { style } : {}), ...shared };
}

export function buildElectricalSheetEntities(raw: unknown): {
  input: z.infer<typeof electricalSheetInputSchema>;
  entities: DrawEntity[];
  dimensions: { width: number; height: number; frame: [number, number, number, number] };
  zones: { columns: number; rows: number; codeFormat: string };
} {
  const input = electricalSheetInputSchema.parse(raw);
  const natural = SHEET_SIZES[input.sheet];
  if (input.extensionModules > 0 && input.sheet !== "A3" && input.sheet !== "A4") {
    throw new Error("Only A3 and A4 sheets may use extensionModules");
  }
  const shortEdge = Math.min(...natural);
  const longEdge = Math.max(...natural) + input.extensionModules * shortEdge;
  const orientation = input.orientation ?? (input.sheet === "A4" ? "portrait" : "landscape");
  const dimensions = orientation === "landscape"
    ? [longEdge, shortEdge] as Point2
    : [shortEdge, longEdge] as Point2;
  const [originX, originY] = input.origin;
  const width = dimensions[0] * input.scale;
  const height = dimensions[1] * input.scale;
  const baseMargin = input.sheet === "A0" || input.sheet === "A1" || input.sheet === "A2" ? 10 : 5;
  const margins = {
    left: (input.margins?.left ?? (input.binding ? 25 : baseMargin)) * input.scale,
    right: (input.margins?.right ?? baseMargin) * input.scale,
    top: (input.margins?.top ?? baseMargin) * input.scale,
    bottom: (input.margins?.bottom ?? baseMargin) * input.scale,
  };
  const left = originX + margins.left;
  const right = originX + width - margins.right;
  const bottom = originY + margins.bottom;
  const top = originY + height - margins.top;
  if (left >= right || bottom >= top) throw new Error("Sheet margins leave no drawable frame area");

  const columns = input.zoneColumns ?? DEFAULT_ZONES[input.sheet][0];
  const rows = input.zoneRows ?? DEFAULT_ZONES[input.sheet][1];
  const shared = common(input.layer, input.colorIndex);
  const entities: DrawEntity[] = [
    rectangle(originX, originY, originX + width, originY + height, shared),
    rectangle(left, bottom, right, top, shared),
  ];
  const tick = Math.min(5 * input.scale, margins.top * 0.65, margins.right * 0.65);
  const zoneTextHeight = Math.min(input.textHeight * input.scale, Math.max(1.5 * input.scale, tick * 0.6));
  for (let column = 0; column < columns; column += 1) {
    const centerX = left + (column + 0.5) * (right - left) / columns;
    const boundaryX = left + column * (right - left) / columns;
    if (column > 0) {
      entities.push({ type: "line", start: [boundaryX, top], end: [boundaryX, top + tick], ...shared });
      entities.push({ type: "line", start: [boundaryX, bottom], end: [boundaryX, bottom - tick], ...shared });
    }
    entities.push(sheetText([centerX - zoneTextHeight * 0.3, top + tick * 0.25], String(column + 1), zoneTextHeight, input.textStyle, shared));
  }
  for (let row = 0; row < rows; row += 1) {
    const centerY = top - (row + 0.5) * (top - bottom) / rows;
    const boundaryY = top - row * (top - bottom) / rows;
    if (row > 0) {
      entities.push({ type: "line", start: [left, boundaryY], end: [left - tick, boundaryY], ...shared });
      entities.push({ type: "line", start: [right, boundaryY], end: [right + tick, boundaryY], ...shared });
    }
    entities.push(sheetText([left - tick * 0.75, centerY - zoneTextHeight * 0.45], String.fromCharCode(65 + row), zoneTextHeight, input.textStyle, shared));
  }

  const blockHeight = Math.min(50 * input.scale, (top - bottom) * 0.4);
  const blockWidth = Math.min(180 * input.scale, right - left);
  const blockLeft = right - blockWidth;
  const blockTop = bottom + blockHeight;
  const cosignWidth = Math.min(35 * input.scale, blockWidth * 0.22);
  const mainLeft = blockLeft + cosignWidth;
  entities.push(rectangle(blockLeft, bottom, right, blockTop, shared));
  entities.push({ type: "line", start: [mainLeft, bottom], end: [mainLeft, blockTop], ...shared });
  const rowHeight = blockHeight / 5;
  for (let row = 1; row < 5; row += 1) {
    const y = bottom + row * rowHeight;
    entities.push({ type: "line", start: [blockLeft, y], end: [right, y], ...shared });
  }
  const title = input.titleBlock ?? {};
  const labelHeight = Math.min(input.textHeight * input.scale, rowHeight * 0.28);
  const valueHeight = Math.min(input.textHeight * 1.05 * input.scale, rowHeight * 0.36);
  const pad = 1.2 * input.scale;
  const addCellText = (x: number, y: number, label: string, value?: string) => {
    entities.push(sheetText([x + pad, y + rowHeight * 0.57], label, labelHeight, input.textStyle, shared));
    if (value) entities.push(sheetText([x + pad, y + rowHeight * 0.12], value, valueHeight, input.textStyle, shared));
  };
  addCellText(mainLeft, bottom + rowHeight * 4, "图样名称", title.drawingName);
  addCellText(mainLeft, bottom + rowHeight * 3, "项目", title.project);
  addCellText(mainLeft, bottom + rowHeight * 2, "图号", title.drawingNumber);
  if (title.retrievalNumber) entities.push(sheetText([right - blockWidth * 0.3, bottom + rowHeight * 2.15], `检索号 ${title.retrievalNumber}`, labelHeight, input.textStyle, shared));
  addCellText(mainLeft, bottom + rowHeight, "版本 / 张次", [title.revision, title.sheetIndex && title.sheetCount ? `${title.sheetIndex}/${title.sheetCount}` : title.sheetIndex].filter(Boolean).join("  "));
  if (title.date) entities.push(sheetText([right - blockWidth * 0.28, bottom + rowHeight * 1.15], `日期 ${title.date}`, labelHeight, input.textStyle, shared));
  addCellText(mainLeft, bottom, "设计 / 校核 / 审核", [title.designedBy, title.checkedBy, title.approvedBy].filter(Boolean).join(" / "));
  if (title.organization) entities.push(sheetText([right - blockWidth * 0.38, bottom + rowHeight * 0.15], title.organization, labelHeight, input.textStyle, shared));

  entities.push(sheetText([blockLeft + pad, blockTop - rowHeight * 0.43], "会签栏", labelHeight, input.textStyle, shared));
  input.cosigners.forEach((cosigner, index) => {
    const y = blockTop - (index + 2) * rowHeight + rowHeight * 0.2;
    entities.push(sheetText([blockLeft + pad, y], `${cosigner.discipline} ${cosigner.name} ${cosigner.date}`, Math.min(labelHeight, cosignWidth / 14), input.textStyle, shared));
  });

  return {
    input,
    entities,
    dimensions: { width, height, frame: [left, bottom, right, top] },
    zones: { columns, rows, codeFormat: "{rowLetter}{columnNumber}" },
  };
}

export async function drawElectricalSheet(bridge: ElectricalBridge, raw: unknown, signal?: AbortSignal): Promise<unknown> {
  const built = buildElectricalSheetEntities(raw);
  const { input } = built;
  const result = await bridge.call("draw", {
    entities: built.entities,
    space: input.space,
    coordinateSystem: input.coordinateSystem,
    expectedDocumentId: input.expectedDocumentId,
    expectedRevision: input.expectedRevision,
    visualMode: input.visualMode,
    highlightResults: input.highlightResults,
    zoomToResult: input.zoomToResult,
  }, { signal });
  return {
    sheet: input.sheet,
    dimensions: built.dimensions,
    zones: built.zones,
    entityCount: built.entities.length,
    result,
  };
}
