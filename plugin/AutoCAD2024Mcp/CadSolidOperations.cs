using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Newtonsoft.Json.Linq;

namespace AutoCAD2024Mcp
{
    internal static class CadSolidOperations
    {
        public static JToken Execute(Database db, JObject p)
        {
            string action = (string)p["action"];
            if (action == "create") return Create(db, p);
            if (action == "boolean") return Boolean(db, p);
            throw new CadMcpException("INVALID_ACTION", "Unsupported solid action: " + action);
        }

        private static JToken Create(Database db, JObject p)
        {
            JArray specs = p["primitives"] as JArray;
            if (specs == null || specs.Count == 0) throw new CadMcpException("INVALID_ARGUMENT", "primitives is required for solid creation.");
            var result = new JArray();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var space = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
                foreach (JObject spec in specs)
                {
                    Solid3d solid = Primitive(spec);
                    if (spec["layer"] != null) solid.Layer = (string)spec["layer"];
                    if (spec["colorIndex"] != null) solid.Color = Color.FromColorIndex(ColorMethod.ByAci, (short)(int)spec["colorIndex"]);
                    space.AppendEntity(solid);
                    tr.AddNewlyCreatedDBObject(solid, true);
                    result.Add(new JObject { ["handle"] = solid.Handle.ToString(), ["type"] = "3DSOLID", ["primitive"] = (string)spec["type"] });
                }
                tr.Commit();
            }
            return new JObject { ["created"] = result, ["count"] = result.Count };
        }

        private static Solid3d Primitive(JObject s)
        {
            string type = (string)s["type"];
            var solid = new Solid3d();
            if (type == "box")
            {
                double length = (double)s["length"], width = (double)s["width"], boxHeight = (double)s["height"];
                solid.CreateBox(length, width, boxHeight);
                solid.TransformBy(Matrix3d.Displacement(Point(s["origin"]).GetAsVector() + new Vector3d(length / 2, width / 2, boxHeight / 2)));
                return solid;
            }
            if (type == "sphere")
            {
                solid.CreateSphere((double)s["radius"]);
                solid.TransformBy(Matrix3d.Displacement(Point(s["center"]).GetAsVector()));
                return solid;
            }
            Vector3d axis = s["axis"] == null ? Vector3d.ZAxis : Vector(s["axis"]);
            if (axis.Length < 1e-9) throw new CadMcpException("INVALID_AXIS", "Solid axis cannot be zero.");
            double height = (double)s["height"];
            if (type == "cylinder") solid.CreateFrustum(height, (double)s["radius"], (double)s["radius"], (double)s["radius"]);
            else if (type == "cone") solid.CreateFrustum(height, (double)s["baseRadius"], (double)s["baseRadius"], (double)s["topRadius"]);
            else throw new CadMcpException("UNSUPPORTED_PRIMITIVE", "Unsupported solid primitive: " + type);
            solid.TransformBy(Matrix3d.Displacement(new Vector3d(0, 0, -height / 2)));
            using (var plane = new Plane(Point(s["center"]), axis.GetNormal())) solid.TransformBy(Matrix3d.PlaneToWorld(plane));
            return solid;
        }

        private static JToken Boolean(Database db, JObject p)
        {
            string targetHandle = (string)p["targetHandle"];
            JArray tools = p["toolHandles"] as JArray;
            string operation = (string)p["operation"];
            if (string.IsNullOrWhiteSpace(targetHandle) || tools == null || tools.Count == 0 || string.IsNullOrWhiteSpace(operation))
                throw new CadMcpException("INVALID_ARGUMENT", "boolean requires operation, targetHandle, and toolHandles.");
            BooleanOperationType op = operation == "union" ? BooleanOperationType.BoolUnite : operation == "subtract" ? BooleanOperationType.BoolSubtract : operation == "intersect" ? BooleanOperationType.BoolIntersect : throw new CadMcpException("INVALID_OPERATION", "Unsupported boolean operation: " + operation);
            bool eraseTools = (bool?)p["eraseTools"] ?? true;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                Solid3d target = OpenSolid(db, tr, targetHandle, OpenMode.ForWrite);
                foreach (JToken token in tools)
                {
                    Solid3d source = OpenSolid(db, tr, (string)token, eraseTools ? OpenMode.ForWrite : OpenMode.ForRead);
                    Solid3d operand = eraseTools ? source : (Solid3d)source.Clone();
                    target.BooleanOperation(op, operand);
                    if (eraseTools && !source.IsErased) source.Erase();
                    if (!eraseTools) operand.Dispose();
                }
                tr.Commit();
                return new JObject { ["handle"] = target.Handle.ToString(), ["operation"] = operation, ["toolCount"] = tools.Count };
            }
        }

        private static Solid3d OpenSolid(Database db, Transaction tr, string text, OpenMode mode)
        {
            if (!long.TryParse(text, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out long value)) throw new CadMcpException("INVALID_HANDLE", "Invalid solid handle: " + text);
            ObjectId id;
            try { id = db.GetObjectId(false, new Handle(value), 0); }
            catch { throw new CadMcpException("HANDLE_NOT_FOUND", "Solid handle not found: " + text); }
            Solid3d solid = tr.GetObject(id, mode, false) as Solid3d;
            if (solid == null) throw new CadMcpException("NOT_A_SOLID", "Entity is not a 3DSOLID: " + text);
            return solid;
        }

        private static Point3d Point(JToken t) => new Point3d((double)t[0], (double)t[1], t.Count() > 2 ? (double)t[2] : 0);
        private static Vector3d Vector(JToken t) => new Vector3d((double)t[0], (double)t[1], (double)t[2]);
    }
}
