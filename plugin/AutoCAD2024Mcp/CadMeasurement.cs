using System;
using System.Globalization;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Newtonsoft.Json.Linq;

namespace AutoCAD2024Mcp
{
    internal static class CadMeasurement
    {
        public static JToken Execute(Database db, JObject p)
        {
            JArray handles = p["handles"] as JArray;
            if (handles == null || handles.Count == 0) throw new CadMcpException("INVALID_ARGUMENT", "handles is required.");
            double? density = (double?)p["density"];
            var items = new JArray();
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                foreach (JToken token in handles)
                {
                    string handle = (string)token;
                    if (!long.TryParse(handle, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long value)) throw new CadMcpException("INVALID_HANDLE", "Invalid entity handle: " + handle);
                    ObjectId id;
                    try { id = db.GetObjectId(false, new Handle(value), 0); }
                    catch { throw new CadMcpException("HANDLE_NOT_FOUND", "Entity handle not found: " + handle); }
                    Entity entity = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                    if (entity == null) continue;
                    var item = new JObject { ["handle"] = handle, ["type"] = entity.GetRXClass().DxfName };
                    try { Extents3d e = entity.GeometricExtents; item["extents"] = new JObject { ["min"] = Point(e.MinPoint), ["max"] = Point(e.MaxPoint) }; } catch { }
                    if (entity is Curve curve) { try { item["length"] = curve.GetDistanceAtParameter(curve.EndParam); } catch { } }
                    if (entity is Circle circle) item["area"] = circle.Area;
                    else if (entity is Polyline polyline && polyline.Closed) { try { item["area"] = polyline.Area; } catch { } }
                    else if (entity is Region region) { try { item["area"] = region.Area; } catch { } }
                    else if (entity is Solid3d solid)
                    {
                        try
                        {
                            Solid3dMassProperties mass = solid.MassProperties;
                            item["volume"] = mass.Volume;
                            item["centroid"] = Point(mass.Centroid);
                            if (density.HasValue) item["mass"] = mass.Volume * density.Value;
                        }
                        catch (System.Exception ex) { item["measurementWarning"] = ex.Message; }
                    }
                    items.Add(item);
                }
            }
            return new JObject { ["count"] = items.Count, ["items"] = items, ["density"] = density };
        }

        private static JArray Point(Point3d p) => new JArray(p.X, p.Y, p.Z);
    }
}
