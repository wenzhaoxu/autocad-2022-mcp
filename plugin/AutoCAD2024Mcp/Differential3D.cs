using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace AutoCAD2024Mcp
{
    internal static class Differential3D
    {
        private sealed class Part
        {
            public string Name;
            public string Layer;
            public short Color;
            public List<Entity> Entities = new List<Entity>();
        }

        public static void Create(Document doc, string outputPath)
        {
            if (!Path.IsPathRooted(outputPath)) throw new CadMcpException("INVALID_PATH", "Output path must be absolute.");
            outputPath = Path.GetFullPath(outputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));

            Database db = doc.Database;
            using (doc.LockDocument())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var parts = new List<Part>
                {
                    BuildRingGear(),
                    BuildHousing(),
                    BuildSideGear("DIFF_SIDE_GEAR_LEFT", "3D_SIDE_GEAR_LEFT", 3, -34, 1),
                    BuildSideGear("DIFF_SIDE_GEAR_RIGHT", "3D_SIDE_GEAR_RIGHT", 4, 34, -1),
                    BuildPlanetGear("DIFF_PLANET_GEAR_TOP", "3D_PLANET_TOP", 2, 34, -1),
                    BuildPlanetGear("DIFF_PLANET_GEAR_BOTTOM", "3D_PLANET_BOTTOM", 2, -34, 1),
                    BuildCrossShaft(),
                    BuildAxle("DIFF_AXLE_LEFT", "3D_AXLE_LEFT", 5, -155, -1),
                    BuildAxle("DIFF_AXLE_RIGHT", "3D_AXLE_RIGHT", 5, 155, 1),
                    BuildBearing("DIFF_BEARING_LEFT", "3D_BEARING_LEFT", 6, -94),
                    BuildBearing("DIFF_BEARING_RIGHT", "3D_BEARING_RIGHT", 6, 94),
                    BuildDrivePinion(),
                    BuildInputShaft(),
                    BuildFasteners()
                };

                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForWrite);
                BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                foreach (Part part in parts)
                {
                    CreateLayer(tr, db, part.Layer, part.Color);
                    ObjectId definitionId = CreatePartBlock(tr, bt, part);
                    var reference = new BlockReference(Point3d.Origin, definitionId) { Layer = part.Layer };
                    ms.AppendEntity(reference);
                    tr.AddNewlyCreatedDBObject(reference, true);
                }

                AddAssemblyAxes(ms, tr, db);
                AddAssemblyInformation(ms, tr, db);
                SetIsometricView(db);
                tr.Commit();
            }

            db.SaveAs(outputPath, true, DwgVersion.AC1032, db.SecurityParameters);
            doc.Editor.WriteMessage("\n[CAD MCP] 3D differential assembly saved: " + outputPath);
        }

        private static Part BuildRingGear()
        {
            var p = NewPart("DIFF_RING_GEAR", "3D_RING_GEAR", 1);
            p.Entities.Add(AnnulusX(20, 127, 104, Point3d.Origin));
            p.Entities.Add(AnnulusX(24, 111, 101, Point3d.Origin));
            for (int i = 0; i < 36; i++)
            {
                double a = i * Math.PI * 2 / 36;
                var tooth = new Solid3d();
                tooth.CreateBox(24, 9, 11);
                tooth.TransformBy(Matrix3d.Displacement(new Vector3d(-12, 117, -5.5)));
                tooth.TransformBy(Matrix3d.Rotation(a, Vector3d.XAxis, Point3d.Origin));
                p.Entities.Add(tooth);
            }
            for (int i = 0; i < 12; i++)
            {
                double a = i * Math.PI * 2 / 12;
                p.Entities.Add(Cylinder(3.2, 24, Vector3d.XAxis, PolarYZ(107, a)));
            }
            return p;
        }

        private static Part BuildHousing()
        {
            var p = NewPart("DIFF_HOUSING", "3D_HOUSING", 8);
            var shell = new Solid3d(); shell.CreateSphere(82);
            var inner = new Solid3d(); inner.CreateSphere(66);
            shell.BooleanOperation(BooleanOperationType.BoolSubtract, inner);
            var leftOpening = Cylinder(31, 190, Vector3d.XAxis, Point3d.Origin);
            shell.BooleanOperation(BooleanOperationType.BoolSubtract, leftOpening);
            var ringClearance = Cylinder(72, 34, Vector3d.XAxis, Point3d.Origin);
            shell.BooleanOperation(BooleanOperationType.BoolSubtract, ringClearance);
            shell.Transparency = new Transparency(115);
            p.Entities.Add(shell);
            p.Entities.Add(AnnulusX(18, 42, 29, new Point3d(-77, 0, 0)));
            p.Entities.Add(AnnulusX(18, 42, 29, new Point3d(77, 0, 0)));
            for (int i = 0; i < 8; i++)
            {
                double a = i * Math.PI / 4;
                Point3d c = PolarYZ(56, a);
                p.Entities.Add(Cylinder(3.5, 22, Vector3d.XAxis, c + new Vector3d(-11, 0, 0)));
            }
            return p;
        }

        private static Part BuildSideGear(string name, string layer, short color, double x, int inward)
        {
            var p = NewPart(name, layer, color);
            Vector3d axis = inward > 0 ? Vector3d.XAxis : -Vector3d.XAxis;
            Point3d center = new Point3d(x, 0, 0);
            p.Entities.Add(Frustum(29, 34, 19, axis, center));
            p.Entities.Add(Annulus(24, 18, 9, axis, center - axis * 20));
            for (int i = 0; i < 16; i++)
            {
                double a = i * Math.PI * 2 / 16;
                var tooth = new Solid3d(); tooth.CreateBox(10, 7, 9);
                tooth.TransformBy(Matrix3d.Displacement(new Vector3d(-5, 26, -4.5)));
                tooth.TransformBy(Matrix3d.Rotation(a, Vector3d.XAxis, Point3d.Origin));
                tooth.TransformBy(Matrix3d.Rotation(inward > 0 ? Math.PI / 2 : -Math.PI / 2, Vector3d.YAxis, Point3d.Origin));
                tooth.TransformBy(Matrix3d.Displacement(center.GetAsVector()));
                p.Entities.Add(tooth);
            }
            return p;
        }

        private static Part BuildPlanetGear(string name, string layer, short color, double y, int inward)
        {
            var p = NewPart(name, layer, color);
            Vector3d axis = inward > 0 ? Vector3d.YAxis : -Vector3d.YAxis;
            Point3d center = new Point3d(0, y, 0);
            p.Entities.Add(Frustum(22, 25, 12, axis, center));
            p.Entities.Add(Annulus(18, 11, 5, axis, center - axis * 14));
            for (int i = 0; i < 12; i++)
            {
                double a = i * Math.PI * 2 / 12;
                Point3d around = new Point3d(20 * Math.Cos(a), y, 20 * Math.Sin(a));
                p.Entities.Add(Cylinder(2.7, 8, axis, around));
            }
            return p;
        }

        private static Part BuildCrossShaft()
        {
            var p = NewPart("DIFF_CROSS_SHAFT", "3D_CROSS_SHAFT", 30);
            p.Entities.Add(Cylinder(5, 112, Vector3d.YAxis, Point3d.Origin));
            p.Entities.Add(Cylinder(5, 112, Vector3d.ZAxis, Point3d.Origin));
            var hub = new Solid3d(); hub.CreateSphere(9); p.Entities.Add(hub);
            return p;
        }

        private static Part BuildAxle(string name, string layer, short color, double centerX, int direction)
        {
            var p = NewPart(name, layer, color);
            Vector3d axis = direction > 0 ? Vector3d.XAxis : -Vector3d.XAxis;
            Point3d center = new Point3d(centerX, 0, 0);
            p.Entities.Add(Cylinder(10, 190, axis, center));
            p.Entities.Add(Cylinder(15, 12, axis, center - axis * 100));
            for (int i = 0; i < 12; i++)
            {
                double a = i * Math.PI * 2 / 12;
                Point3d c = center - axis * 89 + new Vector3d(0, 11 * Math.Cos(a), 11 * Math.Sin(a));
                p.Entities.Add(Cylinder(1.2, 28, axis, c));
            }
            return p;
        }

        private static Part BuildBearing(string name, string layer, short color, double x)
        {
            var p = NewPart(name, layer, color);
            p.Entities.Add(AnnulusX(18, 31, 22, new Point3d(x, 0, 0)));
            p.Entities.Add(AnnulusX(18, 18, 12, new Point3d(x, 0, 0)));
            for (int i = 0; i < 12; i++)
            {
                double a = i * Math.PI * 2 / 12;
                Point3d c = new Point3d(x, 24.5 * Math.Cos(a), 24.5 * Math.Sin(a));
                p.Entities.Add(Cylinder(3, 14, Vector3d.XAxis, c));
            }
            return p;
        }

        private static Part BuildDrivePinion()
        {
            var p = NewPart("DIFF_DRIVE_PINION", "3D_DRIVE_PINION", 1);
            Point3d center = new Point3d(0, 0, -126);
            p.Entities.Add(Frustum(34, 29, 13, Vector3d.ZAxis, center));
            p.Entities.Add(Cylinder(10, 42, -Vector3d.ZAxis, center - new Vector3d(0, 0, 35)));
            for (int i = 0; i < 14; i++)
            {
                double a = i * Math.PI * 2 / 14;
                Point3d c = center + new Vector3d(21 * Math.Cos(a), 21 * Math.Sin(a), 4);
                p.Entities.Add(Cylinder(2.6, 15, Vector3d.ZAxis, c));
            }
            return p;
        }

        private static Part BuildInputShaft()
        {
            var p = NewPart("DIFF_INPUT_SHAFT", "3D_INPUT_SHAFT", 5);
            p.Entities.Add(Cylinder(9, 135, -Vector3d.ZAxis, new Point3d(0, 0, -185)));
            p.Entities.Add(Cylinder(24, 12, -Vector3d.ZAxis, new Point3d(0, 0, -255)));
            p.Entities.Add(Annulus(18, 21, 11, Vector3d.ZAxis, new Point3d(0, 0, -177)));
            return p;
        }

        private static Part BuildFasteners()
        {
            var p = NewPart("DIFF_FASTENERS", "3D_FASTENERS", 2);
            for (int i = 0; i < 12; i++)
            {
                double a = i * Math.PI * 2 / 12;
                Point3d c = PolarYZ(107, a) + new Vector3d(-15, 0, 0);
                p.Entities.Add(Cylinder(2.2, 30, Vector3d.XAxis, c));
            }
            return p;
        }

        private static void AddAssemblyAxes(BlockTableRecord ms, Transaction tr, Database db)
        {
            CreateLayer(tr, db, "3D_CENTER", 5);
            var x = new Line(new Point3d(-270,0,0), new Point3d(270,0,0)) { Layer = "3D_CENTER" };
            var z = new Line(new Point3d(0,0,-300), new Point3d(0,0,160)) { Layer = "3D_CENTER" };
            ms.AppendEntity(x); tr.AddNewlyCreatedDBObject(x, true);
            ms.AppendEntity(z); tr.AddNewlyCreatedDBObject(z, true);
        }

        private static void AddAssemblyInformation(BlockTableRecord ms, Transaction tr, Database db)
        {
            CreateLayer(tr, db, "3D_ANNOTATION", 7);
            var title = new MText
            {
                Layer = "3D_ANNOTATION",
                Location = new Point3d(-220, -175, 145),
                TextHeight = 10,
                Width = 440,
                Contents = "汽车开放式锥齿轮差速器三维组件模型\\P组件: 壳体 / 齿圈 / 半轴齿轮 / 行星齿轮 / 十字轴 / 半轴 / 轴承 / 主动锥齿轮 / 输入轴\\P单位: mm    图号: MCP-DIFF-3D-001"
            };
            ms.AppendEntity(title); tr.AddNewlyCreatedDBObject(title, true);
        }

        private static ObjectId CreatePartBlock(Transaction tr, BlockTable bt, Part part)
        {
            if (bt.Has(part.Name)) return bt[part.Name];
            var block = new BlockTableRecord { Name = part.Name, Origin = Point3d.Origin };
            ObjectId id = bt.Add(block); tr.AddNewlyCreatedDBObject(block, true);
            foreach (Entity entity in part.Entities)
            {
                entity.Layer = part.Layer;
                entity.Color = Color.FromColorIndex(ColorMethod.ByLayer, 256);
                block.AppendEntity(entity); tr.AddNewlyCreatedDBObject(entity, true);
            }
            return id;
        }

        private static Part NewPart(string name, string layer, short color) => new Part { Name = name, Layer = layer, Color = color };

        private static Solid3d AnnulusX(double length, double outerRadius, double innerRadius, Point3d center) => Annulus(length, outerRadius, innerRadius, Vector3d.XAxis, center);

        private static Solid3d Annulus(double length, double outerRadius, double innerRadius, Vector3d axis, Point3d center)
        {
            Solid3d outer = Cylinder(outerRadius, length, axis, center);
            Solid3d inner = Cylinder(innerRadius, length + 4, axis, center);
            outer.BooleanOperation(BooleanOperationType.BoolSubtract, inner);
            return outer;
        }

        private static Solid3d Cylinder(double radius, double length, Vector3d axis, Point3d center)
        {
            var solid = new Solid3d(); solid.CreateFrustum(length, radius, radius, radius);
            OrientFromZ(solid, axis);
            solid.TransformBy(Matrix3d.Displacement(center.GetAsVector() - axis.GetNormal() * (length / 2)));
            return solid;
        }

        private static Solid3d Frustum(double length, double baseRadius, double topRadius, Vector3d axis, Point3d center)
        {
            var solid = new Solid3d(); solid.CreateFrustum(length, baseRadius, baseRadius, topRadius);
            OrientFromZ(solid, axis);
            solid.TransformBy(Matrix3d.Displacement(center.GetAsVector() - axis.GetNormal() * (length / 2)));
            return solid;
        }

        private static void OrientFromZ(Solid3d solid, Vector3d axis)
        {
            Vector3d n = axis.GetNormal();
            if (n.IsEqualTo(Vector3d.XAxis)) solid.TransformBy(Matrix3d.Rotation(Math.PI / 2, Vector3d.YAxis, Point3d.Origin));
            else if (n.IsEqualTo(-Vector3d.XAxis)) solid.TransformBy(Matrix3d.Rotation(-Math.PI / 2, Vector3d.YAxis, Point3d.Origin));
            else if (n.IsEqualTo(Vector3d.YAxis)) solid.TransformBy(Matrix3d.Rotation(-Math.PI / 2, Vector3d.XAxis, Point3d.Origin));
            else if (n.IsEqualTo(-Vector3d.YAxis)) solid.TransformBy(Matrix3d.Rotation(Math.PI / 2, Vector3d.XAxis, Point3d.Origin));
            else if (n.IsEqualTo(-Vector3d.ZAxis)) solid.TransformBy(Matrix3d.Rotation(Math.PI, Vector3d.XAxis, Point3d.Origin));
        }

        private static Point3d PolarYZ(double radius, double angle) => new Point3d(0, radius * Math.Cos(angle), radius * Math.Sin(angle));

        private static void CreateLayer(Transaction tr, Database db, string name, short color)
        {
            LayerTable table = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (table.Has(name)) return;
            table.UpgradeOpen();
            var layer = new LayerTableRecord { Name = name, Color = Color.FromColorIndex(ColorMethod.ByAci, color), LineWeight = LineWeight.LineWeight025 };
            table.Add(layer); tr.AddNewlyCreatedDBObject(layer, true);
        }

        private static void SetIsometricView(Database db)
        {
            db.TileMode = true;
            db.Pdmode = 3;
            db.Extmin = new Point3d(-290, -190, -320);
            db.Extmax = new Point3d(290, 190, 190);
        }
    }
}
