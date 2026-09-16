using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Reflection;
using System.Runtime.InteropServices;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.ApplicationServices.Core;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.PlottingServices;
using Autodesk.AutoCAD.Runtime;
using Newtonsoft.Json.Linq;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CadEntity = Autodesk.AutoCAD.DatabaseServices.Entity;

namespace AutoCAD2024Mcp
{
    internal static class CadDispatcher
    {
        public static Task<JToken> DispatchAsync(string method, JObject p)
        {
            if (IsDrawingCopy(method, p)) return QueueDrawingCopyAsync(method, p);
            if (method == "export" && (string)p["format"] == "dxf") return QueueDxfExportAsync(p);
            if (method == "export" && (string)p["format"] == "stl") return QueueStlExportAsync(p);
            if (IsNativeDocumentSave(method, p)) return QueueNativeDocumentSaveAsync(p);
            if (!RequiresCommandContext(method, p)) return Task.FromResult(Dispatch(method, p));
            return DispatchInCommandContextAsync(method, p);
        }

        private static bool IsNativeDocumentSave(string method, JObject p) =>
            method == "document" && ((string)p["action"] == "save" || (string)p["action"] == "saveAs");

        private static Task<JToken> QueueNativeDocumentSaveAsync(JObject p)
        {
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) throw new CadMcpException("NO_ACTIVE_DOCUMENT", "No active AutoCAD drawing.");
            string action = (string)p["action"];
            string path = null;
            string commandName;
            string commandText;
            if (action == "save")
            {
                if (string.IsNullOrWhiteSpace(doc.Database.Filename)) throw new CadMcpException("UNTITLED_DOCUMENT", "Use saveAs for an untitled drawing.");
                commandName = "QSAVE";
                commandText = "_.QSAVE\r";
            }
            else
            {
                path = AbsolutePath(p, "path", false);
                bool overwrite = (bool?)p["overwrite"] == true;
                if (File.Exists(path))
                {
                    if (!overwrite) throw new CadMcpException("FILE_EXISTS", "Destination exists; set overwrite=true to replace it.");
                    File.Delete(path);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                commandName = "SAVEAS";
                commandText = "_.SAVEAS\r" + SaveCommandVersion((string)p["version"]) + "\r\"" + path.Replace("\"", "") + "\"\r";
            }

            var completion = NewCompletion();
            CommandEventHandler ended = null;
            CommandEventHandler failed = null;
            CommandEventHandler cancelled = null;
            Action detach = () =>
            {
                doc.CommandEnded -= ended;
                doc.CommandFailed -= failed;
                doc.CommandCancelled -= cancelled;
            };
            ended = (sender, args) =>
            {
                if (!string.Equals(args.GlobalCommandName, commandName, StringComparison.OrdinalIgnoreCase)) return;
                detach();
                if (action == "saveAs" && !File.Exists(path)) completion.TrySetException(new CadMcpException("SAVE_FAILED", "AutoCAD completed SAVEAS without creating: " + path));
                else completion.TrySetResult(DocumentResult(doc));
            };
            failed = (sender, args) =>
            {
                if (!string.Equals(args.GlobalCommandName, commandName, StringComparison.OrdinalIgnoreCase)) return;
                detach(); completion.TrySetException(new CadMcpException("SAVE_FAILED", "AutoCAD " + commandName + " command failed."));
            };
            cancelled = (sender, args) =>
            {
                if (!string.Equals(args.GlobalCommandName, commandName, StringComparison.OrdinalIgnoreCase)) return;
                detach(); completion.TrySetException(new CadMcpException("CANCELLED", "AutoCAD " + commandName + " command was cancelled."));
            };
            doc.CommandEnded += ended;
            doc.CommandFailed += failed;
            doc.CommandCancelled += cancelled;
            doc.SendStringToExecute(commandText, true, false, false);
            return completion.Task;
        }

        private static string SaveCommandVersion(string value)
        {
            switch (value)
            {
                case "2000": return "2000";
                case "2004": return "2004";
                case "2007": return "2007";
                case "2010": return "2010";
                case "2013": return "2013";
                default: return "2018";
            }
        }

        private static bool IsDrawingCopy(string method, JObject p) =>
            (method == "document" && (string)p["action"] == "saveCopy") ||
            (method == "export" && (string)p["format"] == "dwg");

        private static Task<JToken> QueueDxfExportAsync(JObject p)
        {
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) throw new CadMcpException("NO_ACTIVE_DOCUMENT", "No active AutoCAD drawing.");
            string path = AbsolutePath(p, "path", false);
            bool overwrite = (bool?)p["overwrite"] == true;
            if (File.Exists(path))
            {
                if (!overwrite) throw new CadMcpException("FILE_EXISTS", "Destination exists; set overwrite=true to replace it.");
                File.Delete(path);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var completion = NewCompletion();
            CommandEventHandler ended = null;
            CommandEventHandler failed = null;
            CommandEventHandler cancelled = null;
            Action detach = () =>
            {
                doc.CommandEnded -= ended;
                doc.CommandFailed -= failed;
                doc.CommandCancelled -= cancelled;
            };
            ended = (sender, args) =>
            {
                if (!string.Equals(args.GlobalCommandName, "DXFOUT", StringComparison.OrdinalIgnoreCase)) return;
                detach();
                if (!File.Exists(path)) completion.TrySetException(new CadMcpException("EXPORT_FAILED", "AutoCAD completed DXFOUT without creating: " + path));
                else completion.TrySetResult(new JObject { ["exported"] = true, ["format"] = "dxf", ["path"] = path });
            };
            failed = (sender, args) =>
            {
                if (!string.Equals(args.GlobalCommandName, "DXFOUT", StringComparison.OrdinalIgnoreCase)) return;
                detach(); completion.TrySetException(new CadMcpException("EXPORT_FAILED", "AutoCAD DXFOUT command failed."));
            };
            cancelled = (sender, args) =>
            {
                if (!string.Equals(args.GlobalCommandName, "DXFOUT", StringComparison.OrdinalIgnoreCase)) return;
                detach(); completion.TrySetException(new CadMcpException("CANCELLED", "AutoCAD DXFOUT command was cancelled."));
            };
            doc.CommandEnded += ended;
            doc.CommandFailed += failed;
            doc.CommandCancelled += cancelled;
            doc.SendStringToExecute("_.DXFOUT\r\"" + path.Replace("\"", "") + "\"\r16\r", true, false, false);
            return completion.Task;
        }

        private static Task<JToken> QueueStlExportAsync(JObject p)
        {
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) throw new CadMcpException("NO_ACTIVE_DOCUMENT", "No active AutoCAD drawing.");
            JArray selection = p["selection"] as JArray;
            if (selection == null || selection.Count == 0) throw new CadMcpException("SELECTION_REQUIRED", "STL export requires 3DSOLID handles in selection.");
            List<ObjectId> ids = IdsFromHandles(doc.Database, selection);
            using (Transaction tr = doc.Database.TransactionManager.StartOpenCloseTransaction())
            {
                foreach (ObjectId id in ids)
                    if (!(tr.GetObject(id, OpenMode.ForRead, false) is Solid3d)) throw new CadMcpException("NOT_A_SOLID", "STL selection contains a non-3DSOLID entity: " + id.Handle);
            }
            string path = AbsolutePath(p, "path", false);
            bool overwrite = (bool?)p["overwrite"] == true;
            if (File.Exists(path))
            {
                if (!overwrite) throw new CadMcpException("FILE_EXISTS", "Destination exists; set overwrite=true to replace it.");
                File.Delete(path);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            doc.Editor.SetImpliedSelection(ids.ToArray());
            var completion = NewCompletion();
            CommandEventHandler ended = null;
            CommandEventHandler failed = null;
            CommandEventHandler cancelled = null;
            Action detach = () =>
            {
                doc.CommandEnded -= ended;
                doc.CommandFailed -= failed;
                doc.CommandCancelled -= cancelled;
            };
            ended = (sender, args) =>
            {
                if (!string.Equals(args.GlobalCommandName, "STLOUT", StringComparison.OrdinalIgnoreCase)) return;
                detach();
                if (!File.Exists(path)) completion.TrySetException(new CadMcpException("EXPORT_FAILED", "AutoCAD completed STLOUT without creating: " + path));
                else completion.TrySetResult(new JObject { ["exported"] = true, ["format"] = "stl", ["path"] = path, ["solidCount"] = ids.Count });
            };
            failed = (sender, args) =>
            {
                if (!string.Equals(args.GlobalCommandName, "STLOUT", StringComparison.OrdinalIgnoreCase)) return;
                detach(); completion.TrySetException(new CadMcpException("EXPORT_FAILED", "AutoCAD STLOUT command failed."));
            };
            cancelled = (sender, args) =>
            {
                if (!string.Equals(args.GlobalCommandName, "STLOUT", StringComparison.OrdinalIgnoreCase)) return;
                detach(); completion.TrySetException(new CadMcpException("CANCELLED", "AutoCAD STLOUT command was cancelled."));
            };
            doc.CommandEnded += ended;
            doc.CommandFailed += failed;
            doc.CommandCancelled += cancelled;
            doc.SendStringToExecute("_.STLOUT\r\r\"" + path.Replace("\"", "") + "\"\r", true, false, false);
            return completion.Task;
        }

        private static Task<JToken> QueueDrawingCopyAsync(string method, JObject p)
        {
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) throw new CadMcpException("NO_ACTIVE_DOCUMENT", "No active AutoCAD drawing.");
            string path = AbsolutePath(p, "path", false);
            bool overwrite = (bool?)p["overwrite"] == true;
            if (File.Exists(path))
            {
                if (!overwrite) throw new CadMcpException("FILE_EXISTS", "Destination exists; set overwrite=true to replace it.");
                File.Delete(path);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path));

            var completion = NewCompletion();
            CommandEventHandler ended = null;
            CommandEventHandler failed = null;
            CommandEventHandler cancelled = null;
            Action detach = () =>
            {
                doc.CommandEnded -= ended;
                doc.CommandFailed -= failed;
                doc.CommandCancelled -= cancelled;
            };
            ended = (sender, args) =>
            {
                if (!IsWblockCommand(args.GlobalCommandName)) return;
                detach();
                if (!File.Exists(path)) completion.TrySetException(new CadMcpException("EXPORT_FAILED", "AutoCAD completed WBLOCK without creating: " + path));
                else if (method == "document") completion.TrySetResult(new JObject { ["savedCopy"] = path, ["activeDocument"] = doc.Name });
                else completion.TrySetResult(new JObject { ["exported"] = true, ["format"] = "dwg", ["path"] = path });
            };
            failed = (sender, args) =>
            {
                if (!IsWblockCommand(args.GlobalCommandName)) return;
                detach(); completion.TrySetException(new CadMcpException("EXPORT_FAILED", "AutoCAD WBLOCK command failed."));
            };
            cancelled = (sender, args) =>
            {
                if (!IsWblockCommand(args.GlobalCommandName)) return;
                detach(); completion.TrySetException(new CadMcpException("CANCELLED", "AutoCAD WBLOCK command was cancelled."));
            };
            doc.CommandEnded += ended;
            doc.CommandFailed += failed;
            doc.CommandCancelled += cancelled;
            doc.SendStringToExecute("_.-WBLOCK\r\"" + path.Replace("\"", "") + "\"\r*\r", true, false, false);
            return completion.Task;
        }

        private static bool IsWblockCommand(string name) =>
            string.Equals(name, "-WBLOCK", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "WBLOCK", StringComparison.OrdinalIgnoreCase);

        private static async Task<JToken> DispatchInCommandContextAsync(string method, JObject p)
        {
            JToken result = null;
            await AcApp.DocumentManager.ExecuteInCommandContextAsync(async ignored =>
            {
                result = Dispatch(method, p);
                await Task.CompletedTask;
            }, null);
            return result;
        }

        private static bool RequiresCommandContext(string method, JObject p)
        {
            if (method == "document")
            {
                string action = (string)p["action"];
                return action == "new" || action == "open" || action == "close" || action == "activate";
            }
            return false;
        }

        public static JToken Dispatch(string method, JObject p)
        {
            switch (method)
            {
                case "status": return Status();
                case "document": return DocumentOperation(p);
                case "inspect": return WithDocument(false, (d, db, ed) => Inspect(db, ed, p));
                case "draw": return WithDocument(true, (d, db, ed) => Draw(d, db, ed, p));
                case "solid": return WithDocument(true, (d, db, ed) => CadSolidOperations.Execute(db, p));
                case "measure": return WithDocument(false, (d, db, ed) => CadMeasurement.Execute(db, p));
                case "edit": return WithDocument(true, (d, db, ed) => Edit(db, ed, p));
                case "layer": return WithDocument(true, (d, db, ed) => Layer(db, p));
                case "block": return WithDocument(true, (d, db, ed) => Block(db, p));
                case "layout": return WithDocument(true, (d, db, ed) => LayoutOperation(db, p));
                case "export": return WithDocument(true, (d, db, ed) => Export(d, db, p));
                case "view": return WithDocument(false, (d, db, ed) => View(d, ed, p));
                case "undo": return WithDocument(false, (d, db, ed) => Undo(d, p));
                case "command": return WithDocument(false, (d, db, ed) => Command(d, p));
                default: throw new CadMcpException("METHOD_NOT_FOUND", "Unknown bridge method: " + method);
            }
        }

        private static JToken WithDocument(bool write, Func<Document, Database, Editor, JToken> action)
        {
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) throw new CadMcpException("NO_ACTIVE_DOCUMENT", "No active AutoCAD drawing.");
            if (!write) return action(doc, doc.Database, doc.Editor);
            using (doc.LockDocument()) return action(doc, doc.Database, doc.Editor);
        }

        private static JObject Status()
        {
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            string assemblyPath = Assembly.GetExecutingAssembly().Location;
            bool trustedDeployment = assemblyPath.IndexOf("\\Autodesk\\ApplicationPlugins\\", StringComparison.OrdinalIgnoreCase) >= 0;
            JArray warnings = new JArray();
            if (!trustedDeployment) warnings.Add("Development DLL loaded outside Autodesk ApplicationPlugins; install the versioned bundle to avoid AutoCAD trust prompts.");
            return new JObject
            {
                ["connected"] = true,
                ["mcpVersion"] = "0.0.3",
                ["health"] = "normal",
                ["warnings"] = warnings,
                ["trustedDeployment"] = trustedDeployment,
                ["assemblyPath"] = assemblyPath,
                ["product"] = "AutoCAD",
                ["release"] = AcApp.Version?.ToString(),
                ["activeDocument"] = doc == null ? null : new JObject
                {
                    ["id"] = doc.GetHashCode().ToString("X", CultureInfo.InvariantCulture),
                    ["name"] = doc.Name,
                    ["path"] = doc.Database.Filename,
                    ["readOnly"] = doc.IsReadOnly,
                    ["tileMode"] = doc.Database.TileMode,
                    ["currentLayer"] = SymbolName(doc.Database.Clayer, doc.Database)
                },
                ["documentCount"] = AcApp.DocumentManager.Count,
                ["processId"] = Process.GetCurrentProcess().Id,
                ["pipe"] = "autocad-2024-mcp-" + Process.GetCurrentProcess().Id
            };
        }

        private static JToken DocumentOperation(JObject p)
        {
            string action = RequiredString(p, "action");
            DocumentCollection docs = AcApp.DocumentManager;
            if (action == "list")
            {
                var items = new JArray();
                foreach (Document d in docs) items.Add(new JObject { ["id"] = d.GetHashCode().ToString("X"), ["name"] = d.Name, ["path"] = d.Database.Filename, ["active"] = d == docs.MdiActiveDocument, ["readOnly"] = d.IsReadOnly });
                return new JObject { ["documents"] = items };
            }
            if (action == "new")
            {
                Document d = docs.Add((string)p["template"] ?? "");
                docs.MdiActiveDocument = d;
                return DocumentResult(d);
            }
            if (action == "open")
            {
                string path = AbsolutePath(p, "path", true);
                bool readOnly = (bool?)p["readOnly"] ?? false;
                string password = (string)p["password"];
                if (!string.IsNullOrEmpty(password) && string.Equals(Path.GetExtension(path), ".dxf", StringComparison.OrdinalIgnoreCase))
                    throw new CadMcpException("INVALID_ARGUMENT", "password is only valid when opening DWG or DWT files.");
                Document d;
                try
                {
                    d = string.IsNullOrEmpty(password) ? docs.Open(path, readOnly) : docs.Open(path, readOnly, password);
                }
                catch (COMException ex) when (IsDecryptionFailure(ex))
                {
                    throw new CadMcpException(
                        "DECRYPTION_FAILED",
                        string.IsNullOrEmpty(password)
                            ? "AutoCAD could not decrypt the drawing. If it is password-protected, provide password; otherwise use AutoCAD RECOVER to validate the file."
                            : "AutoCAD could not decrypt the drawing with the supplied password.",
                        new { path, hresult = "0x" + ex.ErrorCode.ToString("X8", CultureInfo.InvariantCulture) });
                }
                catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == ErrorStatus.SecErrorDecryptingData)
                {
                    throw new CadMcpException(
                        "DECRYPTION_FAILED",
                        string.IsNullOrEmpty(password)
                            ? "AutoCAD could not decrypt the drawing. If it is password-protected, provide password; otherwise use AutoCAD RECOVER to validate the file."
                            : "AutoCAD could not decrypt the drawing with the supplied password.",
                        new { path, errorStatus = ex.ErrorStatus.ToString() });
                }
                docs.MdiActiveDocument = d;
                return DocumentResult(d);
            }

            Document current = docs.MdiActiveDocument ?? throw new CadMcpException("NO_ACTIVE_DOCUMENT", "No active AutoCAD drawing.");
            if (action == "activate")
            {
                string id = (string)p["documentId"];
                string path = (string)p["path"];
                Document found = docs.Cast<Document>().FirstOrDefault(d => d.GetHashCode().ToString("X").Equals(id, StringComparison.OrdinalIgnoreCase) || (!string.IsNullOrEmpty(path) && string.Equals(d.Name, path, StringComparison.OrdinalIgnoreCase)));
                if (found == null) throw new CadMcpException("DOCUMENT_NOT_FOUND", "The requested document is not open.");
                docs.MdiActiveDocument = found;
                return DocumentResult(found);
            }
            if (action == "save")
            {
                throw new CadMcpException("INVALID_CONTEXT", "save must be dispatched asynchronously.");
            }
            if (action == "saveAs")
            {
                throw new CadMcpException("INVALID_CONTEXT", "saveAs must be dispatched asynchronously.");
            }
            if (action == "saveCopy")
            {
                throw new CadMcpException("INVALID_CONTEXT", "saveCopy must be dispatched asynchronously.");
            }
            if (action == "close")
            {
                string name = current.Name;
                if ((bool?)p["discardChanges"] == true) current.CloseAndDiscard();
                else
                {
                    if (string.IsNullOrWhiteSpace(current.Database.Filename)) throw new CadMcpException("UNTITLED_DOCUMENT", "Use saveAs before closing an untitled drawing.");
                    current.CloseAndSave(current.Database.Filename);
                }
                return new JObject { ["closed"] = name };
            }
            throw new CadMcpException("INVALID_ACTION", "Unsupported document action: " + action);
        }

        private static JObject DocumentResult(Document d) => new JObject { ["id"] = d.GetHashCode().ToString("X"), ["name"] = d.Name, ["path"] = d.Database.Filename, ["readOnly"] = d.IsReadOnly };

        private static JToken Inspect(Database db, Editor ed, JObject p)
        {
            string scope = RequiredString(p, "scope");
            if (scope == "drawing") return DrawingInfo(db);
            if (scope == "entities") return InspectEntities(db, p);
            if (scope == "layers") return ListTable<LayerTable, LayerTableRecord>(db, db.LayerTableId, r => new JObject { ["name"] = r.Name, ["colorIndex"] = r.Color.ColorIndex, ["linetype"] = SymbolName(r.LinetypeObjectId, db), ["on"] = !r.IsOff, ["frozen"] = r.IsFrozen, ["locked"] = r.IsLocked, ["plottable"] = r.IsPlottable });
            if (scope == "linetypes") return ListNames<LinetypeTable, LinetypeTableRecord>(db, db.LinetypeTableId);
            if (scope == "textStyles") return ListNames<TextStyleTable, TextStyleTableRecord>(db, db.TextStyleTableId);
            if (scope == "dimensionStyles") return ListNames<DimStyleTable, DimStyleTableRecord>(db, db.DimStyleTableId);
            if (scope == "blocks") return ListBlocks(db);
            if (scope == "layouts") return ListLayouts(db);
            if (scope == "materials") return ListMaterials(db);
            if (scope == "systemVariables")
            {
                var values = new JObject();
                foreach (JToken n in p["names"] as JArray ?? new JArray())
                {
                    string name = (string)n;
                    try { values[name] = JToken.FromObject(AcApp.GetSystemVariable(name)); }
                    catch (System.Exception ex) { values[name] = new JObject { ["error"] = ex.Message }; }
                }
                return values;
            }
            throw new CadMcpException("INVALID_SCOPE", "Unsupported inspection scope: " + scope);
        }

        private static JObject DrawingInfo(Database db)
        {
            Extents3d? ext = null;
            try { ext = new Extents3d(db.Extmin, db.Extmax); } catch { }
            return new JObject { ["filename"] = db.Filename, ["dwgVersion"] = db.OriginalFileVersion.ToString(), ["units"] = db.Insunits.ToString(), ["currentLayer"] = SymbolName(db.Clayer, db), ["currentSpace"] = db.TileMode ? "model" : "paper", ["extents"] = ext.HasValue ? ExtentsJson(ext.Value) : null, ["lastSaved"] = db.Tdupdate };
        }

        private static JToken InspectEntities(Database db, JObject p)
        {
            int offset = (int?)p["offset"] ?? 0;
            int limit = Math.Min((int?)p["limit"] ?? 500, 5000);
            List<ObjectId> ids = ResolveTargets(db, p, true);
            var entities = new JArray();
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                foreach (ObjectId id in ids.Skip(offset).Take(limit))
                {
                    var ent = tr.GetObject(id, OpenMode.ForRead, false) as CadEntity;
                    if (ent != null) entities.Add(EntityJson(ent, (bool?)p["includeGeometry"] ?? true, (bool?)p["includeExtents"] ?? true));
                }
            }
            return new JObject { ["total"] = ids.Count, ["offset"] = offset, ["count"] = entities.Count, ["entities"] = entities };
        }

        private static JToken Draw(Document doc, Database db, Editor ed, JObject p)
        {
            var created = new JArray();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                ObjectId spaceId = ResolveSpace(db, bt, (string)p["space"]);
                var space = (BlockTableRecord)tr.GetObject(spaceId, OpenMode.ForWrite);
                foreach (JObject spec in p["entities"] as JArray ?? new JArray())
                {
                    if ((string)spec["type"] == "hatch")
                    {
                        var hatch = new Hatch();
                        space.AppendEntity(hatch);
                        tr.AddNewlyCreatedDBObject(hatch, true);
                        hatch.SetDatabaseDefaults(db);
                        hatch.SetHatchPattern(HatchPatternType.PreDefined, (string)spec["pattern"] ?? "SOLID");
                        if (spec["scale"] != null) hatch.PatternScale = (double)spec["scale"];
                        if (spec["angle"] != null) hatch.PatternAngle = (double)spec["angle"];
                        hatch.Associative = (bool?)spec["associative"] ?? true;
                        var boundary = new ObjectIdCollection(IdsFromHandles(db, spec["boundaryHandles"] as JArray).ToArray());
                        hatch.AppendLoop(HatchLoopTypes.Default, boundary);
                        hatch.EvaluateHatch(true);
                        ApplyCommon(hatch, spec);
                        created.Add(new JObject { ["handle"] = hatch.Handle.ToString(), ["type"] = hatch.GetRXClass().DxfName, ["objectId"] = hatch.ObjectId.ToString() });
                        continue;
                    }
                    CadEntity ent = CreateEntity(tr, db, spec);
                    space.AppendEntity(ent);
                    tr.AddNewlyCreatedDBObject(ent, true);
                    ApplyCommon(ent, spec);
                    created.Add(new JObject { ["handle"] = ent.Handle.ToString(), ["type"] = ent.GetRXClass().DxfName, ["objectId"] = ent.ObjectId.ToString() });
                }
                tr.Commit();
            }
            if ((bool?)p["zoomToResult"] == true) doc.SendStringToExecute("_.ZOOM\r_E\r", true, false, false);
            return new JObject { ["created"] = created, ["count"] = created.Count };
        }

        private static CadEntity CreateEntity(Transaction tr, Database db, JObject s)
        {
            string type = RequiredString(s, "type");
            if (type == "line") return new Line(P3(s["start"]), P3(s["end"]));
            if (type == "circle") return new Circle(P3(s["center"]), Vector3d.ZAxis, (double)s["radius"]);
            if (type == "arc") return new Arc(P3(s["center"]), (double)s["radius"], (double)s["startAngle"], (double)s["endAngle"]);
            if (type == "point") return new DBPoint(P3(s["position"]));
            if (type == "polyline")
            {
                var pl = new Polyline(); int i = 0;
                foreach (JToken item in (JArray)s["points"]) pl.AddVertexAt(i++, P2(item), 0, (double?)s["width"] ?? 0, (double?)s["width"] ?? 0);
                pl.Closed = (bool?)s["closed"] ?? false; return pl;
            }
            if (type == "ellipse") return new Ellipse(P3(s["center"]), Vector3d.ZAxis, V3(s["majorAxis"]), (double)s["radiusRatio"], (double?)s["startParameter"] ?? 0, (double?)s["endParameter"] ?? Math.PI * 2);
            if (type == "text") return new DBText { Position = P3(s["position"]), TextString = (string)s["text"], Height = (double)s["height"], Rotation = (double?)s["rotation"] ?? 0 };
            if (type == "mtext") return new MText { Location = P3(s["position"]), Contents = (string)s["text"], TextHeight = (double)s["height"], Width = (double)s["width"], Rotation = (double?)s["rotation"] ?? 0 };
            if (type == "blockReference")
            {
                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                string name = RequiredString(s, "name"); if (!bt.Has(name)) throw new CadMcpException("BLOCK_NOT_FOUND", "Block not found: " + name);
                var br = new BlockReference(P3(s["insertionPoint"]), bt[name]) { Rotation = (double?)s["rotation"] ?? 0 };
                if (s["scale"] != null) { Vector3d scale = V3(s["scale"]); br.ScaleFactors = new Scale3d(scale.X, scale.Y, scale.Z); }
                return br;
            }
            if (type == "alignedDimension") return new AlignedDimension(P3(s["first"]), P3(s["second"]), P3(s["dimensionLine"]), "", db.Dimstyle);
            if (type == "rotatedDimension") return new RotatedDimension((double)s["rotation"], P3(s["first"]), P3(s["second"]), P3(s["dimensionLine"]), "", db.Dimstyle);
            throw new CadMcpException("UNSUPPORTED_ENTITY", "Unsupported entity type: " + type);
        }

        private static JToken Edit(Database db, Editor ed, JObject p)
        {
            JObject target = (JObject)p["target"];
            List<ObjectId> ids = ResolveTargets(db, target, false);
            if (ids.Count == 0 && (bool?)p["allowEmpty"] != true) throw new CadMcpException("EMPTY_SELECTION", "No entities matched the edit target.");
            var resulting = new HashSet<ObjectId>(ids);
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (JObject op in p["operations"] as JArray ?? new JArray())
                {
                    string kind = RequiredString(op, "op");
                    foreach (ObjectId id in resulting.ToArray())
                    {
                        var ent = tr.GetObject(id, OpenMode.ForWrite, false) as CadEntity;
                        if (ent == null || ent.IsErased) continue;
                        if (kind == "setProperties") ApplyCommon(ent, op);
                        else if (kind == "move") ent.TransformBy(Matrix3d.Displacement(P3(op["to"]) - P3(op["from"])));
                        else if (kind == "rotate") ent.TransformBy(Matrix3d.Rotation((double)op["angle"], Vector3d.ZAxis, P3(op["base"])));
                        else if (kind == "scale") ent.TransformBy(Matrix3d.Scaling((double)op["factor"], P3(op["base"])));
                        else if (kind == "mirror") { ent.TransformBy(Matrix3d.Mirroring(new Line3d(P3(op["first"]), P3(op["second"])))); if ((bool?)op["eraseSource"] == true) ent.Erase(); }
                        else if (kind == "erase") ent.Erase();
                        else if (kind == "copy")
                        {
                            var copy = (CadEntity)ent.Clone(); copy.TransformBy(Matrix3d.Displacement(P3(op["to"]) - P3(op["from"])));
                            var owner = (BlockTableRecord)tr.GetObject(ent.OwnerId, OpenMode.ForWrite); owner.AppendEntity(copy); tr.AddNewlyCreatedDBObject(copy, true); resulting.Add(copy.ObjectId);
                        }
                        else throw new CadMcpException("INVALID_OPERATION", "Unsupported edit operation: " + kind);
                    }
                }
                tr.Commit();
            }
            return new JObject { ["matched"] = ids.Count, ["resultCount"] = resulting.Count, ["handles"] = new JArray(resulting.Where(id => id.IsValid && !id.IsErased).Select(id => id.Handle.ToString())) };
        }

        private static JToken Layer(Database db, JObject p)
        {
            string action = RequiredString(p, "action"), name = (string)p["name"];
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                LayerTable table = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                if (action == "create")
                {
                    if (string.IsNullOrWhiteSpace(name)) throw new CadMcpException("INVALID_ARGUMENT", "Layer name is required.");
                    if (!table.Has(name)) { table.UpgradeOpen(); var rec = new LayerTableRecord { Name = name }; table.Add(rec); tr.AddNewlyCreatedDBObject(rec, true); ApplyLayer(rec, p["properties"] as JObject); }
                }
                else if (action == "setCurrent") { EnsureLayer(table, name); db.Clayer = table[name]; }
                else if (action == "update") { EnsureLayer(table, name); var rec = (LayerTableRecord)tr.GetObject(table[name], OpenMode.ForWrite); ApplyLayer(rec, p["properties"] as JObject); }
                else if (action == "rename") { EnsureLayer(table, name); var rec = (LayerTableRecord)tr.GetObject(table[name], OpenMode.ForWrite); rec.Name = RequiredString(p, "newName"); }
                else if (action == "delete") { EnsureLayer(table, name); if (table[name] == db.Clayer) throw new CadMcpException("CURRENT_LAYER", "Cannot delete the current layer."); ((LayerTableRecord)tr.GetObject(table[name], OpenMode.ForWrite)).Erase(); }
                else if (action == "isolate") { foreach (ObjectId id in table) { var rec = (LayerTableRecord)tr.GetObject(id, OpenMode.ForWrite); rec.IsOff = !rec.Name.Equals(name, StringComparison.OrdinalIgnoreCase); } }
                else if (action == "restore") { foreach (ObjectId id in table) { var rec = (LayerTableRecord)tr.GetObject(id, OpenMode.ForWrite); rec.IsOff = false; } }
                else throw new CadMcpException("INVALID_ACTION", "Unsupported layer action: " + action);
                tr.Commit();
            }
            return new JObject { ["action"] = action, ["name"] = name, ["ok"] = true };
        }

        private static JToken Block(Database db, JObject p)
        {
            string action = RequiredString(p, "action"), name = RequiredString(p, "name");
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                if (action == "create")
                {
                    if (bt.Has(name)) throw new CadMcpException("BLOCK_EXISTS", "Block already exists: " + name);
                    bt.UpgradeOpen(); var def = new BlockTableRecord { Name = name, Origin = p["basePoint"] == null ? Point3d.Origin : P3(p["basePoint"]) }; bt.Add(def); tr.AddNewlyCreatedDBObject(def, true);
                    foreach (ObjectId id in IdsFromHandles(db, p["sourceHandles"] as JArray)) { var source = (CadEntity)tr.GetObject(id, OpenMode.ForWrite); var clone = (CadEntity)source.Clone(); def.AppendEntity(clone); tr.AddNewlyCreatedDBObject(clone, true); if ((bool?)p["deleteSource"] == true) source.Erase(); }
                }
                else if (action == "insert")
                {
                    if (!bt.Has(name)) throw new CadMcpException("BLOCK_NOT_FOUND", "Block not found: " + name);
                    BlockTableRecord space = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite); var br = new BlockReference(p["insertionPoint"] == null ? Point3d.Origin : P3(p["insertionPoint"]), bt[name]); br.Rotation = (double?)p["rotation"] ?? 0; if (p["scale"] != null) { Vector3d v = V3(p["scale"]); br.ScaleFactors = new Scale3d(v.X, v.Y, v.Z); } space.AppendEntity(br); tr.AddNewlyCreatedDBObject(br, true);
                    tr.Commit(); return new JObject { ["name"] = name, ["handle"] = br.Handle.ToString() };
                }
                else if (action == "import")
                {
                    string path = AbsolutePath(p, "sourcePath", true); using (var source = new Database(false, true)) { source.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, true, ""); db.Insert(name, source, true); }
                }
                else throw new CadMcpException("INVALID_ACTION", "Unsupported block action: " + action);
                tr.Commit();
            }
            return new JObject { ["action"] = action, ["name"] = name, ["ok"] = true };
        }

        private static JToken LayoutOperation(Database db, JObject p)
        {
            string action = RequiredString(p, "action");
            if (action == "list") return ListLayouts(db);
            LayoutManager lm = LayoutManager.Current;
            string name = (string)p["name"];
            if (action == "create") lm.CreateLayout(name);
            else if (action == "delete") lm.DeleteLayout(name);
            else if (action == "rename") lm.RenameLayout(name, RequiredString(p, "newName"));
            else if (action == "activate") lm.CurrentLayout = name;
            else if (action == "configure") ConfigureLayout(db, name, p["settings"] as JObject ?? new JObject());
            else throw new CadMcpException("INVALID_ACTION", "Unsupported layout action: " + action);
            return new JObject { ["action"] = action, ["name"] = name, ["ok"] = true };
        }

        private static void ConfigureLayout(Database db, string name, JObject s)
        {
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                DBDictionary dict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                if (!dict.Contains(name)) throw new CadMcpException("LAYOUT_NOT_FOUND", "Layout not found: " + name);
                Autodesk.AutoCAD.DatabaseServices.Layout layout = (Autodesk.AutoCAD.DatabaseServices.Layout)tr.GetObject(dict.GetAt(name), OpenMode.ForWrite);
                var validator = PlotSettingsValidator.Current;
                if (s["device"] != null) validator.SetPlotConfigurationName(layout, (string)s["device"], (string)s["media"] ?? null);
                if (s["styleSheet"] != null) validator.SetCurrentStyleSheet(layout, (string)s["styleSheet"]);
                if (s["orientation"] != null) validator.SetPlotRotation(layout, (string)s["orientation"] == "landscape" ? PlotRotation.Degrees090 : PlotRotation.Degrees000);
                if ((bool?)s["centered"] != null) validator.SetPlotCentered(layout, (bool)s["centered"]);
                if ((bool?)s["scaleToFit"] == true) validator.SetUseStandardScale(layout, true);
                tr.Commit();
            }
        }

        private static JToken Export(Document doc, Database db, JObject p)
        {
            string format = RequiredString(p, "format"), path = AbsolutePath(p, "path", false);
            if (File.Exists(path) && (bool?)p["overwrite"] != true) throw new CadMcpException("FILE_EXISTS", "Destination exists; set overwrite=true to replace it.");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            if (format == "dwg")
            {
                throw new CadMcpException("INVALID_CONTEXT", "DWG export must be dispatched asynchronously.");
            }
            else if (format == "dxf") throw new CadMcpException("INVALID_CONTEXT", "DXF export must be dispatched asynchronously.");
            else if (format == "stl") throw new CadMcpException("INVALID_CONTEXT", "STL export must be dispatched asynchronously.");
            else
            {
                string cmd = format == "pdf" ? "_.EXPORTPDF" : format == "dwf" ? "_.DWFOUT" : format == "svg" ? "_.SVGOUT" : format == "png" ? "_.PNGOUT" : "_.JPGOUT";
                doc.SendStringToExecute(cmd + " \"" + path.Replace("\"", "") + "\" ", true, false, false);
                return new JObject { ["queued"] = true, ["format"] = format, ["path"] = path, ["note"] = "AutoCAD queued the native export command; completion is asynchronous." };
            }
        }

        private static JToken View(Document doc, Editor ed, JObject p)
        {
            string action = RequiredString(p, "action");
            bool queued = false;
            if (action == "zoomExtents") { doc.SendStringToExecute("_.ZOOM\r_E\r", true, false, false); queued = true; }
            else if (action == "regen") ed.Regen();
            else if (action == "zoomWindow")
            {
                JArray w = (JArray)p["window"];
                Point3d a = P3(w[0]), b = P3(w[1]);
                string command = string.Format(CultureInfo.InvariantCulture, "_.ZOOM\r_W\r{0},{1},{2}\r{3},{4},{5}\r", a.X, a.Y, a.Z, b.X, b.Y, b.Z);
                doc.SendStringToExecute(command, true, false, false); queued = true;
            }
            else if (action == "setView") { doc.SendStringToExecute("_.-VIEW\r_R\r\"" + RequiredString(p, "viewName").Replace("\"", "") + "\"\r", true, false, false); queued = true; }
            else throw new CadMcpException("INVALID_ACTION", "This view action is not implemented in the in-process endpoint: " + action);
            return new JObject { ["action"] = action, ["ok"] = true, ["queued"] = queued };
        }

        private static JToken Undo(Document doc, JObject p)
        {
            string action = RequiredString(p, "action"); int steps = (int?)p["steps"] ?? 1;
            for (int i = 0; i < steps; i++) doc.SendStringToExecute(action == "undo" ? "_.U " : "_.REDO ", true, false, false);
            return new JObject { ["queued"] = true, ["action"] = action, ["steps"] = steps };
        }

        private static readonly HashSet<string> AllowedCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "REGEN", "ZOOM", "-PURGE", "AUDIT", "OVERKILL", "FLATTEN", "WBLOCK", "DXFOUT", "-PLOT", "EXPORTPDF", "PNGOUT", "JPGOUT", "SVGOUT" };
        private static JToken Command(Document doc, JObject p)
        {
            string command = RequiredString(p, "command").Trim().TrimStart('_', '.');
            if (!AllowedCommands.Contains(command)) throw new CadMcpException("COMMAND_BLOCKED", "Command is not allowlisted: " + command, AllowedCommands.OrderBy(x => x));
            var args = p["arguments"] as JArray ?? new JArray();
            string line = "_." + command + " " + string.Join(" ", args.Select(a => QuoteCommandArg((string)a ?? a.ToString()))) + " ";
            doc.SendStringToExecute(line, true, false, false);
            return new JObject { ["queued"] = true, ["command"] = command };
        }

        private static string QuoteCommandArg(string value) => value.Any(char.IsWhiteSpace) ? "\"" + value.Replace("\"", "") + "\"" : value;

        private static List<ObjectId> ResolveTargets(Database db, JObject p, bool defaultAll)
        {
            var handles = p["handles"] as JArray;
            if (handles != null && handles.Count > 0) return IdsFromHandles(db, handles);
            JObject filter = p["filter"] as JObject;
            if (filter == null && !defaultAll) throw new CadMcpException("INVALID_TARGET", "Provide handles or a constrained filter.");
            var result = new List<ObjectId>();
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                foreach (ObjectId spaceId in new[] { bt[BlockTableRecord.ModelSpace], bt[BlockTableRecord.PaperSpace] })
                {
                    var space = (BlockTableRecord)tr.GetObject(spaceId, OpenMode.ForRead);
                    foreach (ObjectId id in space)
                    {
                        var ent = tr.GetObject(id, OpenMode.ForRead, false) as CadEntity;
                        if (ent != null && Matches(ent, filter)) result.Add(id);
                    }
                }
            }
            return result.Distinct().ToList();
        }

        private static bool Matches(CadEntity ent, JObject f)
        {
            if (f == null) return true;
            var types = f["types"] as JArray;
            if (types != null && types.Count > 0 && !types.Any(t => string.Equals((string)t, ent.GetRXClass().DxfName, StringComparison.OrdinalIgnoreCase) || string.Equals((string)t, ent.GetType().Name, StringComparison.OrdinalIgnoreCase))) return false;
            if (f["layer"] != null && !Wildcard(ent.Layer, (string)f["layer"])) return false;
            if (f["colorIndex"] != null && ent.ColorIndex != (short)(int)f["colorIndex"]) return false;
            if (f["textContains"] != null)
            {
                string text = ent is DBText dt ? dt.TextString : ent is MText mt ? mt.Contents : null;
                if (text == null || text.IndexOf((string)f["textContains"], StringComparison.OrdinalIgnoreCase) < 0) return false;
            }
            if (f["window"] is JArray window)
            {
                try { Extents3d ex = ent.GeometricExtents; Point3d a = P3(window[0]), b = P3(window[1]); double minX = Math.Min(a.X, b.X), maxX = Math.Max(a.X, b.X), minY = Math.Min(a.Y, b.Y), maxY = Math.Max(a.Y, b.Y); if (ex.MaxPoint.X < minX || ex.MinPoint.X > maxX || ex.MaxPoint.Y < minY || ex.MinPoint.Y > maxY) return false; } catch { return false; }
            }
            return true;
        }

        private static bool Wildcard(string value, string pattern)
        {
            if (pattern == "*") return true;
            if (!pattern.Contains("*")) return string.Equals(value, pattern, StringComparison.OrdinalIgnoreCase);
            string[] parts = pattern.Split('*'); int at = 0;
            foreach (string part in parts) { if (part.Length == 0) continue; int found = value.IndexOf(part, at, StringComparison.OrdinalIgnoreCase); if (found < 0) return false; at = found + part.Length; }
            return true;
        }

        private static List<ObjectId> IdsFromHandles(Database db, JArray handles)
        {
            var ids = new List<ObjectId>();
            foreach (JToken token in handles ?? new JArray())
            {
                if (!long.TryParse((string)token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long value)) throw new CadMcpException("INVALID_HANDLE", "Invalid entity handle: " + token);
                try { ids.Add(db.GetObjectId(false, new Handle(value), 0)); } catch { throw new CadMcpException("HANDLE_NOT_FOUND", "Entity handle not found: " + token); }
            }
            return ids;
        }

        private static JObject EntityJson(CadEntity e, bool geometry, bool extents)
        {
            var j = new JObject { ["handle"] = e.Handle.ToString(), ["type"] = e.GetRXClass().DxfName, ["class"] = e.GetType().Name, ["layer"] = e.Layer, ["colorIndex"] = e.ColorIndex, ["linetype"] = e.Linetype, ["visible"] = e.Visible };
            if (extents) { try { j["extents"] = ExtentsJson(e.GeometricExtents); } catch { } }
            if (!geometry) return j;
            if (e is Line line) { j["start"] = PointJson(line.StartPoint); j["end"] = PointJson(line.EndPoint); j["length"] = line.Length; }
            else if (e is Circle c) { j["center"] = PointJson(c.Center); j["radius"] = c.Radius; j["circumference"] = c.Circumference; j["area"] = c.Area; }
            else if (e is Arc a) { j["center"] = PointJson(a.Center); j["radius"] = a.Radius; j["startAngle"] = a.StartAngle; j["endAngle"] = a.EndAngle; j["length"] = a.Length; }
            else if (e is Polyline pl) { var points = new JArray(); for (int i = 0; i < pl.NumberOfVertices; i++) points.Add(PointJson(pl.GetPoint3dAt(i))); j["points"] = points; j["closed"] = pl.Closed; j["length"] = pl.Length; if (pl.Closed) { try { j["area"] = pl.Area; } catch { } } }
            else if (e is DBText dt) { j["text"] = dt.TextString; j["position"] = PointJson(dt.Position); j["height"] = dt.Height; j["rotation"] = dt.Rotation; }
            else if (e is MText mt) { j["text"] = mt.Contents; j["position"] = PointJson(mt.Location); j["height"] = mt.TextHeight; j["width"] = mt.Width; j["rotation"] = mt.Rotation; }
            else if (e is BlockReference br) { j["name"] = br.Name; j["insertionPoint"] = PointJson(br.Position); j["rotation"] = br.Rotation; j["scale"] = new JArray(br.ScaleFactors.X, br.ScaleFactors.Y, br.ScaleFactors.Z); }
            return j;
        }

        private static JToken ListBlocks(Database db)
        {
            var result = new JArray(); using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction()) { BlockTable table = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead); foreach (ObjectId id in table) { var r = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead); if (!r.IsLayout) result.Add(new JObject { ["name"] = r.Name, ["anonymous"] = r.IsAnonymous, ["xref"] = r.IsFromExternalReference, ["entityCount"] = r.Cast<ObjectId>().Count() }); } } return new JObject { ["blocks"] = result };
        }

        private static JToken ListLayouts(Database db)
        {
            var result = new JArray(); using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction()) { DBDictionary d = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead); foreach (DBDictionaryEntry entry in d) { var l = (Autodesk.AutoCAD.DatabaseServices.Layout)tr.GetObject(entry.Value, OpenMode.ForRead); result.Add(new JObject { ["name"] = l.LayoutName, ["modelType"] = l.ModelType, ["tabOrder"] = l.TabOrder, ["device"] = l.PlotConfigurationName, ["media"] = l.CanonicalMediaName }); } } return new JObject { ["layouts"] = result };
        }

        private static JToken ListMaterials(Database db)
        {
            var result = new JArray();
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                DBDictionary dictionary = (DBDictionary)tr.GetObject(db.MaterialDictionaryId, OpenMode.ForRead);
                foreach (DBDictionaryEntry entry in dictionary)
                {
                    Material material = tr.GetObject(entry.Value, OpenMode.ForRead) as Material;
                    result.Add(new JObject { ["name"] = entry.Key, ["description"] = material?.Description });
                }
            }
            return new JObject { ["materials"] = result, ["count"] = result.Count };
        }

        private static JToken ListNames<TTable, TRecord>(Database db, ObjectId id) where TTable : SymbolTable where TRecord : SymbolTableRecord => ListTable<TTable, TRecord>(db, id, r => new JObject { ["name"] = r.Name });
        private static JToken ListTable<TTable, TRecord>(Database db, ObjectId id, Func<TRecord, JObject> map) where TTable : SymbolTable where TRecord : SymbolTableRecord
        {
            var result = new JArray(); using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction()) { TTable table = (TTable)tr.GetObject(id, OpenMode.ForRead); foreach (ObjectId item in table) result.Add(map((TRecord)tr.GetObject(item, OpenMode.ForRead))); } return new JObject { ["items"] = result, ["count"] = result.Count };
        }

        private static void ApplyCommon(CadEntity e, JObject p)
        {
            if (p["layer"] != null) e.Layer = (string)p["layer"];
            if (p["colorIndex"] != null) e.Color = Color.FromColorIndex(ColorMethod.ByAci, (short)(int)p["colorIndex"]);
            if (p["linetype"] != null) e.Linetype = (string)p["linetype"];
            if (p["lineweight"] != null) e.LineWeight = (LineWeight)(int)p["lineweight"];
        }

        private static void ApplyLayer(LayerTableRecord r, JObject p)
        {
            if (p == null) return;
            if (p["colorIndex"] != null) r.Color = Color.FromColorIndex(ColorMethod.ByAci, (short)(int)p["colorIndex"]);
            if (p["lineweight"] != null) r.LineWeight = (LineWeight)(int)p["lineweight"];
            if (p["on"] != null) r.IsOff = !(bool)p["on"];
            if (p["frozen"] != null) r.IsFrozen = (bool)p["frozen"];
            if (p["locked"] != null) r.IsLocked = (bool)p["locked"];
            if (p["plottable"] != null) r.IsPlottable = (bool)p["plottable"];
            if (p["description"] != null) r.Description = (string)p["description"];
        }

        private static void EnsureLayer(LayerTable table, string name) { if (string.IsNullOrWhiteSpace(name) || !table.Has(name)) throw new CadMcpException("LAYER_NOT_FOUND", "Layer not found: " + name); }
        private static ObjectId ResolveSpace(Database db, BlockTable bt, string space) => space == "model" ? bt[BlockTableRecord.ModelSpace] : space == "paper" ? bt[BlockTableRecord.PaperSpace] : db.CurrentSpaceId;
        private static string SymbolName(ObjectId id, Database db) { using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction()) return ((SymbolTableRecord)tr.GetObject(id, OpenMode.ForRead)).Name; }
        private static string RequiredString(JObject p, string name) { string value = (string)p[name]; if (string.IsNullOrWhiteSpace(value)) throw new CadMcpException("INVALID_ARGUMENT", name + " is required."); return value; }
        private static string AbsolutePath(JObject p, string name, bool mustExist) { string value = RequiredString(p, name); if (!Path.IsPathRooted(value)) throw new CadMcpException("INVALID_PATH", name + " must be an absolute local path."); value = Path.GetFullPath(value); if (mustExist && !File.Exists(value)) throw new CadMcpException("FILE_NOT_FOUND", "File not found: " + value); return value; }
        private static Point2d P2(JToken t) => new Point2d((double)t[0], (double)t[1]);
        private static Point3d P3(JToken t) => new Point3d((double)t[0], (double)t[1], t.Count() > 2 ? (double)t[2] : 0);
        private static Vector3d V3(JToken t) => new Vector3d((double)t[0], (double)t[1], t.Count() > 2 ? (double)t[2] : 0);
        private static JArray PointJson(Point3d p) => new JArray(p.X, p.Y, p.Z);
        private static JObject ExtentsJson(Extents3d e) => new JObject { ["min"] = PointJson(e.MinPoint), ["max"] = PointJson(e.MaxPoint) };
        private static DwgVersion DwgVersionOf(string value) { switch (value) { case "2013": return DwgVersion.AC1027; case "2010": return DwgVersion.AC1024; case "2007": return DwgVersion.AC1021; case "2004": return DwgVersion.AC1800; case "2000": return DwgVersion.AC1015; default: return DwgVersion.AC1032; } }
        private static bool IsDecryptionFailure(COMException exception) =>
            exception.ErrorCode == unchecked((int)0x80200157) ||
            exception.Message.IndexOf("decrypt", StringComparison.OrdinalIgnoreCase) >= 0 ||
            exception.Message.IndexOf("解密", StringComparison.OrdinalIgnoreCase) >= 0;
        private static TaskCompletionSource<JToken> NewCompletion() => new TaskCompletionSource<JToken>(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
