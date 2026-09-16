import { describe, expect, it } from "vitest";
import {
  buildElectricalSheetEntities,
  buildElectricalSymbolEntities,
  ELECTRICAL_SYMBOL_CATALOG,
  ELECTRICAL_SYMBOLS,
} from "../src/electrical.js";

describe("electrical MCP resources", () => {
  it("publishes a unique, categorized symbol catalog with drawable geometry", () => {
    expect(ELECTRICAL_SYMBOLS.length).toBeGreaterThanOrEqual(25);
    expect(new Set(ELECTRICAL_SYMBOLS.map((symbol) => symbol.id)).size).toBe(ELECTRICAL_SYMBOLS.length);
    expect(ELECTRICAL_SYMBOLS.every((symbol) => symbol.primitives.length > 0)).toBe(true);
    expect(ELECTRICAL_SYMBOL_CATALOG.symbols.some((symbol) => symbol.nameZh === "三相电动机")).toBe(true);
  });

  it("expands, scales, rotates, and labels a symbol as native draw entities", () => {
    const built = buildElectricalSymbolEntities({
      symbolId: "resistor",
      insertionPoint: [100, 50],
      scale: 2,
      rotation: Math.PI / 2,
      tag: "R1",
      layer: "E-SYMBOL",
    });

    expect(built.symbol.nameZh).toBe("电阻器");
    expect(built.entities.at(-1)).toMatchObject({ type: "text", text: "R1", layer: "E-SYMBOL" });
    expect(built.entities[0]).toMatchObject({ type: "line", start: [100, 30], end: [100, 38] });
  });
});

describe("electrical drawing sheet generator", () => {
  it("builds the default A3 landscape frame, title block, cosign block, and zones", () => {
    const built = buildElectricalSheetEntities({
      sheet: "A3",
      titleBlock: {
        drawingName: "电动机控制原理图",
        drawingNumber: "E-01",
        retrievalNumber: "IDX-01",
      },
      cosigners: [{ discipline: "工艺", name: "张三", date: "2026-09-16" }],
    });

    expect(built.dimensions).toEqual({ width: 420, height: 297, frame: [25, 5, 415, 292] });
    expect(built.zones).toEqual({ columns: 8, rows: 6, codeFormat: "{rowLetter}{columnNumber}" });
    expect(built.entities.filter((entity) => entity.type === "polyline")).toHaveLength(3);
    expect(built.entities.some((entity) => entity.text === "图样名称")).toBe(true);
    expect(built.entities.some((entity) => entity.text === "电动机控制原理图")).toBe(true);
    expect(built.entities.some((entity) => entity.text === "会签栏")).toBe(true);
    expect(built.entities.some((entity) => entity.text === "工艺 张三 2026-09-16")).toBe(true);
    expect(built.entities.some((entity) => entity.text === "1")).toBe(true);
    expect(built.entities.some((entity) => entity.text === "A")).toBe(true);
  });

  it("uses portrait by default for A4 and rejects odd zone counts", () => {
    expect(buildElectricalSheetEntities({ sheet: "A4" }).dimensions).toEqual({
      width: 210,
      height: 297,
      frame: [25, 5, 205, 292],
    });
    expect(buildElectricalSheetEntities({ sheet: "A4", orientation: "landscape", extensionModules: 1 }).dimensions.width).toBe(507);
    expect(() => buildElectricalSheetEntities({ sheet: "A3", zoneColumns: 7 })).toThrow("zoneColumns must be even");
    expect(() => buildElectricalSheetEntities({ sheet: "A2", extensionModules: 1 })).toThrow("Only A3 and A4");
  });
});
