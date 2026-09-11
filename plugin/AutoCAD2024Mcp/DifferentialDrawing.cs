using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AcEntity = Autodesk.AutoCAD.DatabaseServices.Entity;

namespace AutoCAD2024Mcp
{
    internal static class DifferentialDrawing
    {
        public static void Create(Document doc, string outputPath)
        {
            if (!Path.IsPathRooted(outputPath)) throw new CadMcpException("INVALID_PATH", "Output path must be absolute.");
            outputPath = Path.GetFullPath(outputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));

            Database db = doc.Database;
            using (doc.LockDocument())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                CreateLayer(tr, db, "OUTLINE", 7, LineWeight.LineWeight035);
                CreateLayer(tr, db, "GEAR", 1, LineWeight.LineWeight025);
                CreateLayer(tr, db, "SHAFT", 3, LineWeight.LineWeight030);
                CreateLayer(tr, db, "BEARING", 4, LineWeight.LineWeight020);
                CreateLayer(tr, db, "CENTER", 5, LineWeight.LineWeight013);
                CreateLayer(tr, db, "ANNOTATION", 2, LineWeight.LineWeight018);
                CreateLayer(tr, db, "TITLE", 6, LineWeight.LineWeight025);

                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                AddCenterLines(ms, tr);
                AddHousing(ms, tr);
                AddRingGear(ms, tr);
                AddSideGears(ms, tr);
                AddPlanetGears(ms, tr);
                AddCrossShaft(ms, tr);
                AddAxlesAndBearings(ms, tr);
                AddDrivePinion(ms, tr);
                AddFasteners(ms, tr);
                AddAnnotations(ms, tr, db);
                AddTitleBlock(ms, tr);

                tr.Commit();
            }

            db.SaveAs(outputPath, true, DwgVersion.AC1032, db.SecurityParameters);
            doc.Editor.WriteMessage("\n[CAD MCP] Differential assembly saved: " + outputPath);
        }

        private static void AddCenterLines(BlockTableRecord ms, Transaction tr)
        {
            Add(ms, tr, new Line(new Point3d(-215, 0, 0), new Point3d(215, 0, 0)), "CENTER");
            Add(ms, tr, new Line(new Point3d(0, -145, 0), new Point3d(0, 145, 0)), "CENTER");
            Add(ms, tr, new Circle(Point3d.Origin, Vector3d.ZAxis, 60), "CENTER");
        }

        private static void AddHousing(BlockTableRecord ms, Transaction tr)
        {
            Add(ms, tr, new Circle(Point3d.Origin, Vector3d.ZAxis, 96), "OUTLINE");
            Add(ms, tr, new Circle(Point3d.Origin, Vector3d.ZAxis, 84), "OUTLINE");
            Add(ms, tr, ClosedPolyline(new[] { P(-92,-28), P(-116,-22), P(-116,22), P(-92,28), P(-80,18), P(-80,-18) }), "OUTLINE");
            Add(ms, tr, ClosedPolyline(new[] { P(92,-28), P(116,-22), P(116,22), P(92,28), P(80,18), P(80,-18) }), "OUTLINE");
            for (int i = 0; i < 8; i++)
            {
                double a = i * Math.PI / 4;
                Point3d p = Polar(0, 0, 88, a);
                Add(ms, tr, new Circle(p, Vector3d.ZAxis, 3.2), "OUTLINE");
            }
        }

        private static void AddRingGear(BlockTableRecord ms, Transaction tr)
        {
            Add(ms, tr, GearOutline(0, 0, 121, 127, 40), "GEAR");
            Add(ms, tr, new Circle(Point3d.Origin, Vector3d.ZAxis, 111), "GEAR");
            Add(ms, tr, new Circle(Point3d.Origin, Vector3d.ZAxis, 103), "GEAR");
            for (int i = 0; i < 12; i++)
            {
                double a = i * Math.PI / 6;
                Add(ms, tr, new Circle(Polar(0, 0, 107, a), Vector3d.ZAxis, 2.4), "GEAR");
            }
        }

        private static void AddSideGears(BlockTableRecord ms, Transaction tr)
        {
            foreach (double x in new[] { -42.0, 42.0 })
            {
                Add(ms, tr, GearOutline(x, 0, 29, 34, 18), "GEAR");
                Add(ms, tr, new Circle(new Point3d(x, 0, 0), Vector3d.ZAxis, 18), "GEAR");
                Add(ms, tr, new Circle(new Point3d(x, 0, 0), Vector3d.ZAxis, 11), "SHAFT");
                for (int i = 0; i < 12; i++)
                {
                    double a = i * Math.PI / 6;
                    Point3d p1 = Polar(x, 0, 11, a);
                    Point3d p2 = Polar(x, 0, 17, a);
                    Add(ms, tr, new Line(p1, p2), "SHAFT");
                }
            }
        }

        private static void AddPlanetGears(BlockTableRecord ms, Transaction tr)
        {
            foreach (double y in new[] { -43.0, 43.0 })
            {
                Add(ms, tr, GearOutline(0, y, 20, 25, 14), "GEAR");
                Add(ms, tr, new Circle(new Point3d(0, y, 0), Vector3d.ZAxis, 8), "SHAFT");
            }
        }

        private static void AddCrossShaft(BlockTableRecord ms, Transaction tr)
        {
            Add(ms, tr, ClosedPolyline(new[] { P(-5,-70), P(5,-70), P(5,70), P(-5,70) }), "SHAFT");
            Add(ms, tr, ClosedPolyline(new[] { P(-70,-4), P(70,-4), P(70,4), P(-70,4) }), "SHAFT");
            Add(ms, tr, new Circle(Point3d.Origin, Vector3d.ZAxis, 7), "SHAFT");
        }

        private static void AddAxlesAndBearings(BlockTableRecord ms, Transaction tr)
        {
            Add(ms, tr, ClosedPolyline(new[] { P(-205,-10), P(-16,-10), P(-16,10), P(-205,10) }), "SHAFT");
            Add(ms, tr, ClosedPolyline(new[] { P(16,-10), P(205,-10), P(205,10), P(16,10) }), "SHAFT");
            foreach (double x in new[] { -106.0, 106.0 })
            {
                Add(ms, tr, new Circle(new Point3d(x, 0, 0), Vector3d.ZAxis, 25), "BEARING");
                Add(ms, tr, new Circle(new Point3d(x, 0, 0), Vector3d.ZAxis, 16), "BEARING");
                for (int i = 0; i < 10; i++) Add(ms, tr, new Circle(Polar(x, 0, 20.5, i * Math.PI / 5), Vector3d.ZAxis, 2.7), "BEARING");
            }
            for (int i = -4; i <= 4; i++)
            {
                Add(ms, tr, new Line(new Point3d(-194 + i * 4, -10, 0), new Point3d(-190 + i * 4, 10, 0)), "SHAFT");
                Add(ms, tr, new Line(new Point3d(158 + i * 4, -10, 0), new Point3d(162 + i * 4, 10, 0)), "SHAFT");
            }
        }

        private static void AddDrivePinion(BlockTableRecord ms, Transaction tr)
        {
            Add(ms, tr, GearOutline(141, 0, 17, 24, 12), "GEAR");
            Add(ms, tr, new Circle(new Point3d(141, 0, 0), Vector3d.ZAxis, 9), "SHAFT");
            Add(ms, tr, ClosedPolyline(new[] { P(141,-7), P(208,-7), P(208,7), P(141,7) }), "SHAFT");
            Add(ms, tr, new Circle(new Point3d(171, 0, 0), Vector3d.ZAxis, 14), "BEARING");
            Add(ms, tr, new Circle(new Point3d(171, 0, 0), Vector3d.ZAxis, 9), "BEARING");
            Add(ms, tr, ClosedPolyline(new[] { P(202,-12), P(214,-12), P(214,12), P(202,12) }), "OUTLINE");
        }

        private static void AddFasteners(BlockTableRecord ms, Transaction tr)
        {
            foreach (double a in new[] { Math.PI/4, 3*Math.PI/4, 5*Math.PI/4, 7*Math.PI/4 })
            {
                Point3d p = Polar(0, 0, 75, a);
                Add(ms, tr, Polygon(p.X, p.Y, 4, 6), "OUTLINE");
            }
        }

        private static void AddAnnotations(BlockTableRecord ms, Transaction tr, Database db)
        {
            Text(ms, tr, -195, 145, "汽车开放式锥齿轮差速器总成", 8, "TITLE");
            Text(ms, tr, -195, 134, "装配剖视示意图  比例 1:1  单位: mm", 4.5, "ANNOTATION");
            Leader(ms, tr, -78, 110, -140, 120, "1 主减速器齿圈");
            Leader(ms, tr, -42, 32, -128, 74, "2 左半轴齿轮");
            Leader(ms, tr, 42, 32, 96, 78, "3 右半轴齿轮");
            Leader(ms, tr, 12, 52, 72, 105, "4 行星锥齿轮");
            Leader(ms, tr, 5, 70, 55, 125, "5 十字轴");
            Leader(ms, tr, -106, -24, -155, -70, "6 圆锥滚子轴承");
            Leader(ms, tr, 141, 24, 164, 64, "7 输入主动锥齿轮");
            Leader(ms, tr, 80, -58, 150, -88, "8 差速器壳体");

            Add(ms, tr, new RotatedDimension(0, new Point3d(-127,0,0), new Point3d(127,0,0), new Point3d(0,162,0), "", db.Dimstyle), "ANNOTATION");
            Add(ms, tr, new RotatedDimension(Math.PI/2, new Point3d(0,-127,0), new Point3d(0,127,0), new Point3d(-225,0,0), "", db.Dimstyle), "ANNOTATION");
            Add(ms, tr, new RotatedDimension(0, new Point3d(-205,0,0), new Point3d(214,0,0), new Point3d(0,-155,0), "", db.Dimstyle), "ANNOTATION");
        }

        private static void AddTitleBlock(BlockTableRecord ms, Transaction tr)
        {
            double x0 = -215, y0 = -220, x1 = 215, y1 = -170;
            Add(ms, tr, ClosedPolyline(new[] { P(x0,y0), P(x1,y0), P(x1,y1), P(x0,y1) }), "TITLE");
            foreach (double x in new[] { -80.0, 80.0, 150.0 }) Add(ms, tr, new Line(P(x,y0), P(x,y1)), "TITLE");
            Add(ms, tr, new Line(P(-215,-190), P(215,-190)), "TITLE");
            Text(ms, tr, -208, -184, "零件明细", 5, "TITLE");
            Text(ms, tr, -208, -199, "1 齿圈  2/3 半轴齿轮  4 行星齿轮  5 十字轴", 4, "ANNOTATION");
            Text(ms, tr, -208, -213, "6 轴承  7 主动锥齿轮  8 壳体  9 左/右半轴", 4, "ANNOTATION");
            Text(ms, tr, -72, -184, "材料", 4, "TITLE");
            Text(ms, tr, -72, -203, "20CrMnTi / QT500-7", 4, "ANNOTATION");
            Text(ms, tr, 86, -184, "图号", 4, "TITLE");
            Text(ms, tr, 86, -203, "MCP-DIFF-001", 5, "ANNOTATION");
            Text(ms, tr, 157, -184, "版本", 4, "TITLE");
            Text(ms, tr, 166, -207, "A", 7, "TITLE");
        }

        private static void Leader(BlockTableRecord ms, Transaction tr, double x1, double y1, double x2, double y2, string label)
        {
            Add(ms, tr, new Line(P(x1,y1), P(x2,y2)), "ANNOTATION");
            Add(ms, tr, new Line(P(x2,y2), P(x2 + 42,y2)), "ANNOTATION");
            Text(ms, tr, x2 + 2, y2 + 2, label, 4, "ANNOTATION");
        }

        private static void Text(BlockTableRecord ms, Transaction tr, double x, double y, string value, double height, string layer)
        {
            Add(ms, tr, new DBText { Position = P(x,y), TextString = value, Height = height }, layer);
        }

        private static Polyline GearOutline(double cx, double cy, double rootRadius, double tipRadius, int teeth)
        {
            var pl = new Polyline(); int n = teeth * 4;
            for (int i = 0; i < n; i++)
            {
                int phase = i % 4;
                double radius = phase == 1 || phase == 2 ? tipRadius : rootRadius;
                double a = i * 2 * Math.PI / n;
                pl.AddVertexAt(i, new Point2d(cx + radius * Math.Cos(a), cy + radius * Math.Sin(a)), 0, 0, 0);
            }
            pl.Closed = true; return pl;
        }

        private static Polyline Polygon(double cx, double cy, double radius, int sides)
        {
            var pl = new Polyline();
            for (int i = 0; i < sides; i++) pl.AddVertexAt(i, new Point2d(cx + radius * Math.Cos(i * 2 * Math.PI / sides), cy + radius * Math.Sin(i * 2 * Math.PI / sides)), 0, 0, 0);
            pl.Closed = true; return pl;
        }

        private static Polyline ClosedPolyline(IEnumerable<Point3d> points)
        {
            var pl = new Polyline(); int i = 0;
            foreach (Point3d p in points) pl.AddVertexAt(i++, new Point2d(p.X, p.Y), 0, 0, 0);
            pl.Closed = true; return pl;
        }

        private static void CreateLayer(Transaction tr, Database db, string name, short color, LineWeight weight)
        {
            LayerTable table = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (table.Has(name)) return;
            table.UpgradeOpen();
            var record = new LayerTableRecord { Name = name, Color = Color.FromColorIndex(ColorMethod.ByAci, color), LineWeight = weight };
            table.Add(record); tr.AddNewlyCreatedDBObject(record, true);
        }

        private static T Add<T>(BlockTableRecord ms, Transaction tr, T entity, string layer) where T : AcEntity
        {
            entity.Layer = layer; ms.AppendEntity(entity); tr.AddNewlyCreatedDBObject(entity, true); return entity;
        }

        private static Point3d P(double x, double y) => new Point3d(x, y, 0);
        private static Point3d Polar(double cx, double cy, double r, double a) => new Point3d(cx + r * Math.Cos(a), cy + r * Math.Sin(a), 0);
    }
}
