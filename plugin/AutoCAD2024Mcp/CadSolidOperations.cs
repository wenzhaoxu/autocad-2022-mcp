using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.ApplicationServices.Core;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Newtonsoft.Json.Linq;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AutoCAD2024Mcp
{
    internal static class CadSolidOperations
    {
        public static JToken Execute(Database db, JObject p)
        {
            string action = (string)p["action"];
            if (action == "create") return Create(db, p);
            if (action == "boolean") return Boolean(db, p);
            if (action == "extrude" || action == "revolve" || action == "sweep") return Feature(db, p, action);
            throw new CadMcpException("INVALID_ACTION", "Unsupported solid action: " + action);
        }

        private static JToken Create(Database db, JObject p)
        {
            JArray specs = p["primitives"] as JArray;
            if (specs == null || specs.Count == 0) throw new CadMcpException("INVALID_ARGUMENT", "primitives is required for solid creation.");
            var result = new JArray();
            bool stepMode = string.Equals((string)p["visualMode"], "step", StringComparison.OrdinalIgnoreCase);
            if (!stepMode)
            {
                using (Transaction transaction = db.TransactionManager.StartTransaction())
                {
                    foreach (JObject spec in specs) result.Add(AppendPrimitive(db, transaction, spec, out ObjectId ignored));
                    transaction.Commit();
                }
            }
            else
            {
                try
                {
                    int index = 0;
                    foreach (JObject spec in specs)
                    {
                        ObjectId id;
                        JObject item;
                        using (Transaction transaction = db.TransactionManager.StartTransaction())
                        {
                            item = AppendPrimitive(db, transaction, spec, out id);
                            transaction.Commit();
                        }
                        result.Add(item);
                        index++;
                        PresentStep(db, id, index, specs.Count, (string)spec["type"]);
                    }
                }
                catch (System.Exception exception)
                {
                    Cleanup(db, result);
                    throw new CadMcpException("SOLID_STEP_FAILED", "Step-mode solid creation failed and committed solids were removed.",
                        new JObject { ["completedSteps"] = result.Count, ["cause"] = exception.Message });
                }
            }
            return new JObject { ["created"] = result, ["count"] = result.Count, ["visualMode"] = stepMode ? "step" : "batch" };
        }

        private static JObject AppendPrimitive(Database db, Transaction transaction, JObject spec, out ObjectId id)
        {
            var space = (BlockTableRecord)transaction.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
            Solid3d solid = Primitive(spec);
            if (spec["layer"] != null) solid.Layer = (string)spec["layer"];
            if (spec["colorIndex"] != null) solid.Color = Color.FromColorIndex(ColorMethod.ByAci, (short)(int)spec["colorIndex"]);
            space.AppendEntity(solid);
            transaction.AddNewlyCreatedDBObject(solid, true);
            id = solid.ObjectId;
            return new JObject { ["handle"] = solid.Handle.ToString(), ["type"] = "3DSOLID", ["primitive"] = (string)spec["type"] };
        }

        private static void PresentStep(Database db, ObjectId id, int index, int total, string primitive)
        {
            Document document = AcApp.DocumentManager.MdiActiveDocument;
            if (document == null || !ReferenceEquals(document.Database, db)) return;
            try { document.Editor.Regen(); AcApp.UpdateScreen(); } catch { }
            try { document.Editor.SetImpliedSelection(new[] { id }); } catch { }
            document.Editor.WriteMessage("\n[CAD MCP] Solid step " + index + "/" + total + ": " + primitive + ".");
            CadEventJournal.RecordPresentation(document, "step", new JObject
            {
                ["index"] = index,
                ["total"] = total,
                ["primitive"] = primitive,
                ["handle"] = id.Handle.ToString()
            });
        }

        private static void Cleanup(Database db, JArray created)
        {
            using (Transaction transaction = db.TransactionManager.StartTransaction())
            {
                foreach (JObject item in created.OfType<JObject>())
                {
                    if (!long.TryParse((string)item["handle"], System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture, out long value)) continue;
                    try
                    {
                        ObjectId id = db.GetObjectId(false, new Handle(value), 0);
                        DBObject entity = transaction.GetObject(id, OpenMode.ForWrite, false);
                        if (!entity.IsErased) entity.Erase();
                    }
                    catch { }
                }
                transaction.Commit();
            }
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
            if (type == "wedge")
            {
                solid.CreateWedge((double)s["length"], (double)s["width"], (double)s["height"]);
                Point3d origin = Point(s["origin"]);
                Extents3d extents = solid.GeometricExtents;
                solid.TransformBy(Matrix3d.Displacement(origin - extents.MinPoint));
                return solid;
            }
            Vector3d axis = s["axis"] == null ? Vector3d.ZAxis : Vector(s["axis"]);
            if (axis.Length < 1e-9) throw new CadMcpException("INVALID_AXIS", "Solid axis cannot be zero.");
            if (type == "torus")
            {
                solid.CreateTorus((double)s["majorRadius"], (double)s["minorRadius"]);
                using (var plane = new Plane(Point(s["center"]), axis.GetNormal())) solid.TransformBy(Matrix3d.PlaneToWorld(plane));
                return solid;
            }
            double height = (double)s["height"];
            if (type == "cylinder") solid.CreateFrustum(height, (double)s["radius"], (double)s["radius"], (double)s["radius"]);
            else if (type == "cone") solid.CreateFrustum(height, (double)s["baseRadius"], (double)s["baseRadius"], (double)s["topRadius"]);
            else if (type == "pyramid") solid.CreatePyramid(height, (int)s["sides"], (double)s["baseRadius"], (double?)s["topRadius"] ?? 0);
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

        private static JToken Feature(Database db, JObject p, string action)
        {
            string profileHandle = (string)p["profileHandle"];
            if (string.IsNullOrWhiteSpace(profileHandle)) throw new CadMcpException("INVALID_ARGUMENT", "profileHandle is required for " + action + ".");
            bool eraseSource = (bool?)p["eraseSource"] ?? false;
            try
            {
                using (Transaction transaction = db.TransactionManager.StartTransaction())
                {
                    Entity profile = OpenEntity(db, transaction, profileHandle, eraseSource ? OpenMode.ForWrite : OpenMode.ForRead);
                    var solid = new Solid3d();
                    if (action == "extrude")
                    {
                        Vector3d direction;
                        if (p["direction"] != null) direction = Vector(p["direction"]);
                        else direction = Vector3d.ZAxis.MultiplyBy((double?)p["height"] ?? throw new CadMcpException("INVALID_ARGUMENT", "height or direction is required for extrude."));
                        if (direction.Length < 1e-9) throw new CadMcpException("INVALID_ARGUMENT", "extrude direction cannot be zero.");
                        solid.CreateExtrudedSolid(profile, direction, new SweepOptions());
                    }
                    else if (action == "revolve")
                    {
                        Point3d axisPoint = Point(p["axisPoint"] ?? throw new CadMcpException("INVALID_ARGUMENT", "axisPoint is required for revolve."));
                        Vector3d axis = Vector(p["axis"] ?? throw new CadMcpException("INVALID_ARGUMENT", "axis is required for revolve."));
                        if (axis.Length < 1e-9) throw new CadMcpException("INVALID_ARGUMENT", "revolve axis cannot be zero.");
                        solid.CreateRevolvedSolid(profile, axisPoint, axis.GetNormal(), (double?)p["angle"] ?? Math.PI * 2,
                            (double?)p["startAngle"] ?? 0, new RevolveOptions());
                    }
                    else
                    {
                        string pathHandle = (string)p["pathHandle"];
                        if (string.IsNullOrWhiteSpace(pathHandle)) throw new CadMcpException("INVALID_ARGUMENT", "pathHandle is required for sweep.");
                        Entity path = OpenEntity(db, transaction, pathHandle, OpenMode.ForRead);
                        solid.CreateSweptSolid(profile, path, new SweepOptions());
                        if ((bool?)p["erasePath"] == true && !path.IsWriteEnabled) path.UpgradeOpen();
                        if ((bool?)p["erasePath"] == true) path.Erase();
                    }

                    if (p["layer"] != null) solid.Layer = (string)p["layer"];
                    if (p["colorIndex"] != null) solid.Color = Color.FromColorIndex(ColorMethod.ByAci, (short)(int)p["colorIndex"]);
                    var space = (BlockTableRecord)transaction.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
                    space.AppendEntity(solid);
                    transaction.AddNewlyCreatedDBObject(solid, true);
                    if (eraseSource) profile.Erase();
                    transaction.Commit();
                    return new JObject
                    {
                        ["handle"] = solid.Handle.ToString(),
                        ["type"] = "3DSOLID",
                        ["operation"] = action,
                        ["sourceHandle"] = profileHandle
                    };
                }
            }
            catch (CadMcpException) { throw; }
            catch (System.Exception exception)
            {
                throw new CadMcpException("SOLID_FEATURE_FAILED", "AutoCAD could not create the requested " + action + " solid.", exception.Message);
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

        private static Entity OpenEntity(Database db, Transaction transaction, string text, OpenMode mode)
        {
            if (!long.TryParse(text, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out long value))
                throw new CadMcpException("INVALID_HANDLE", "Invalid entity handle: " + text);
            ObjectId id;
            try { id = db.GetObjectId(false, new Handle(value), 0); }
            catch { throw new CadMcpException("HANDLE_NOT_FOUND", "Entity handle not found: " + text); }
            Entity entity = transaction.GetObject(id, mode, false) as Entity;
            if (entity == null) throw new CadMcpException("NOT_AN_ENTITY", "Object is not a drawable entity: " + text);
            return entity;
        }

        private static Point3d Point(JToken t) => new Point3d((double)t[0], (double)t[1], t.Count() > 2 ? (double)t[2] : 0);
        private static Vector3d Vector(JToken t) => new Vector3d((double)t[0], (double)t[1], (double)t[2]);
    }
}
