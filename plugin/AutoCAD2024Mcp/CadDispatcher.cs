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
        private const string ChineseTextStyleName = "MCP_CJK";
        private const int DefaultOperationTimeoutMs = 120000;
        public static Task<JToken> DispatchAsync(string method, JObject p, string requestId = null, int timeoutMs = DefaultOperationTimeoutMs)
        {
            if (method == "events") return Task.FromResult<JToken>(CadEventJournal.Read(p));
            string operationId = string.IsNullOrWhiteSpace(requestId) ? Guid.NewGuid().ToString("N") : requestId;
            Document document = AcApp.DocumentManager.MdiActiveDocument;
            CadEventJournal.OperationStarted(operationId, method, document);
            try
            {
                Task<JToken> task;
                using (CadEventJournal.BeginOperation(operationId, method))
                {
                    ValidateMutationContext(method, p, document);
                    if (IsDrawingCopy(method, p)) task = QueueDrawingCopyAsync(method, p);
                    else if (method == "export" && (string)p["format"] == "dxf") task = QueueDxfExportAsync(p);
                    else if (method == "export" && (string)p["format"] == "stl") task = QueueStlExportAsync(p);
                    else if (method == "export") task = QueueNativeExportAsync(p);
                    else if (IsNativeDocumentSave(method, p)) task = QueueNativeDocumentSaveAsync(p);
                    else if (RequiresQueuedCommand(method, p)) task = QueueInteractiveOperationAsync(method, p);
                    else if (!RequiresCommandContext(method, p)) task = Task.FromResult(DispatchAndPresent(method, p));
                    else task = DispatchInCommandContextAsync(method, p);
                }
                return ObserveOperationAsync(task, operationId, method, document, timeoutMs);
            }
            catch (System.Exception exception)
            {
                CadEventJournal.OperationCompleted(operationId, method, document, false, ErrorCode(exception), exception.Message);
                throw;
            }
        }

        private static async Task<JToken> ObserveOperationAsync(Task<JToken> task, string operationId, string method, Document document, int requestedTimeoutMs)
        {
            try
            {
                int operationTimeoutMs = Math.Max(1000, Math.Min(requestedTimeoutMs - 1000, 1800000));
                Task completed = await Task.WhenAny(task, Task.Delay(operationTimeoutMs)).ConfigureAwait(false);
                if (!ReferenceEquals(completed, task))
                {
                    _ = task.ContinueWith(value => { var ignored = value.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                    throw new CadMcpException("COMMAND_TIMEOUT", "AutoCAD did not finish the operation within " + operationTimeoutMs + " ms.");
                }
                JToken result = await task.ConfigureAwait(false);
                CadEventJournal.OperationCompleted(operationId, method, document, true);
                return result;
            }
            catch (System.Exception exception)
            {
                CadEventJournal.OperationCompleted(operationId, method, document, false, ErrorCode(exception), exception.Message);
                throw;
            }
        }

        private static string ErrorCode(System.Exception exception) => exception is CadMcpException known ? known.Code : "CAD_EXCEPTION";

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

        private static Task<JToken> QueueNativeExportAsync(JObject p)
        {
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) throw new CadMcpException("NO_ACTIVE_DOCUMENT", "No active AutoCAD drawing.");
            string format = RequiredString(p, "format").ToLowerInvariant();
            var commands = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["pdf"] = "-EXPORTPDF",
                ["dwf"] = "DWFOUT",
                ["svg"] = "SVGOUT",
                ["png"] = "PNGOUT",
                ["jpeg"] = "JPGOUT"
            };
            if (!commands.TryGetValue(format, out string command))
                throw new CadMcpException("INVALID_FORMAT", "Unsupported native export format: " + format);
            string path = AbsolutePath(p, "path", false);
            if (File.Exists(path))
            {
                if ((bool?)p["overwrite"] != true) throw new CadMcpException("FILE_EXISTS", "Destination exists; set overwrite=true to replace it.");
                File.Delete(path);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string commandText = "_." + command + "\r\"" + path.Replace("\"", "") + "\"\r";
            return QueueCommandCompletion(doc, name => CommandNamesMatch(name, command), commandText, 1, "EXPORT_FAILED", () =>
            {
                if (!File.Exists(path)) throw new CadMcpException("EXPORT_FAILED", "AutoCAD completed " + command + " without creating: " + path);
                return new JObject
                {
                    ["exported"] = true,
                    ["format"] = format,
                    ["path"] = path,
                    ["queued"] = false,
                    ["confirmed"] = true
                };
            });
        }

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

        private static bool RequiresQueuedCommand(string method, JObject p)
        {
            if (method == "undo") return true;
            if (method == "command") return (bool?)p["waitForCompletion"] ?? true;
            if (method == "view" && (string)p["action"] == "capture") return true;
            return false;
        }

        private static Task<JToken> QueueInteractiveOperationAsync(string method, JObject p)
        {
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) throw new CadMcpException("NO_ACTIVE_DOCUMENT", "No active AutoCAD drawing.");
            if (method == "view")
            {
                string path = AbsolutePath(p, "path", false);
                if (File.Exists(path))
                {
                    if ((bool?)p["overwrite"] != true) throw new CadMcpException("FILE_EXISTS", "Destination exists; set overwrite=true to replace it.");
                    File.Delete(path);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                return QueueCommandCompletion(doc, name => string.Equals(name, "PNGOUT", StringComparison.OrdinalIgnoreCase),
                    "_.PNGOUT\r\"" + path.Replace("\"", "") + "\"\r", 1, "CAPTURE_FAILED", () =>
                    {
                        if (!File.Exists(path)) throw new CadMcpException("CAPTURE_FAILED", "AutoCAD completed PNGOUT without creating: " + path);
                        return new JObject { ["captured"] = true, ["path"] = path, ["queued"] = false, ["confirmed"] = true };
                    });
            }
            if (method == "undo")
            {
                string action = RequiredString(p, "action");
                int steps = (int?)p["steps"] ?? 1;
                string command = action == "undo" ? "U" : action == "redo" ? "REDO" : null;
                if (command == null) throw new CadMcpException("INVALID_ACTION", "Undo action must be undo or redo.");
                string commandText = string.Concat(Enumerable.Repeat("_." + command + "\r", steps));
                return QueueCommandCompletion(doc, name => string.Equals(name, command, StringComparison.OrdinalIgnoreCase),
                    commandText, steps, "UNDO_FAILED", () => new JObject
                    {
                        ["queued"] = false,
                        ["confirmed"] = true,
                        ["action"] = action,
                        ["steps"] = steps
                    });
            }

            string requested = RequiredString(p, "command").Trim().TrimStart('_', '.');
            if (!AllowedCommands.Contains(requested))
                throw new CadMcpException("COMMAND_BLOCKED", "Command is not allowlisted: " + requested, AllowedCommands.OrderBy(x => x));
            JArray arguments = p["arguments"] as JArray ?? new JArray();
            string line = "_." + requested + "\r" + string.Join("\r", arguments.Select(value => QuoteCommandArg((string)value ?? value.ToString()))) + "\r";
            return QueueCommandCompletion(doc, name => CommandNamesMatch(name, requested), line, 1, "COMMAND_FAILED", () => new JObject
            {
                ["queued"] = false,
                ["confirmed"] = true,
                ["command"] = requested
            });
        }

        private static Task<JToken> QueueCommandCompletion(Document doc, Func<string, bool> matches, string commandText,
            int requiredCompletions, string failureCode, Func<JToken> success)
        {
            var completion = NewCompletion();
            int completed = 0;
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
                if (!matches(args.GlobalCommandName)) return;
                completed++;
                if (completed < requiredCompletions) return;
                detach();
                try { completion.TrySetResult(success()); }
                catch (System.Exception exception) { completion.TrySetException(exception); }
            };
            failed = (sender, args) =>
            {
                if (!matches(args.GlobalCommandName)) return;
                detach();
                completion.TrySetException(new CadMcpException(failureCode, "AutoCAD command failed: " + args.GlobalCommandName));
            };
            cancelled = (sender, args) =>
            {
                if (!matches(args.GlobalCommandName)) return;
                detach();
                completion.TrySetException(new CadMcpException("CANCELLED", "AutoCAD command was cancelled: " + args.GlobalCommandName));
            };
            doc.CommandEnded += ended;
            doc.CommandFailed += failed;
            doc.CommandCancelled += cancelled;
            doc.SendStringToExecute(commandText, true, false, false);
            return completion.Task;
        }

        private static bool CommandNamesMatch(string actual, string requested)
        {
            string left = (actual ?? "").TrimStart('_', '.');
            string right = (requested ?? "").TrimStart('_', '.');
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(left.TrimStart('-'), right.TrimStart('-'), StringComparison.OrdinalIgnoreCase);
        }

        private static readonly HashSet<string> MutationMethods = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "draw", "solid", "edit", "layer", "textStyle", "block", "layout"
        };

        private static bool IsMutation(string method, JObject p)
        {
            if (!MutationMethods.Contains(method)) return false;
            string action = (string)p["action"];
            return !(method == "textStyle" && action == "list") && !(method == "layout" && action == "list");
        }

        private static void ValidateMutationContext(string method, JObject p, Document document)
        {
            if (!IsMutation(method, p)) return;
            if (document == null) throw new CadMcpException("NO_ACTIVE_DOCUMENT", "No active AutoCAD drawing.");
            string expectedDocumentId = (string)p["expectedDocumentId"];
            string actualDocumentId = document.GetHashCode().ToString("X", CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(expectedDocumentId) && !string.Equals(expectedDocumentId, actualDocumentId, StringComparison.OrdinalIgnoreCase))
                throw new CadMcpException("DOCUMENT_CONFLICT", "The active AutoCAD document changed before the operation could run.", new JObject
                {
                    ["expectedDocumentId"] = expectedDocumentId,
                    ["actualDocumentId"] = actualDocumentId
                });
            if (p["expectedRevision"] != null)
            {
                long expectedRevision = (long)p["expectedRevision"];
                long actualRevision = CadEventJournal.GetRevision(document);
                if (expectedRevision != actualRevision)
                    throw new CadMcpException("REVISION_CONFLICT", "The drawing changed after it was inspected. Inspect again before editing.", new JObject
                    {
                        ["expectedRevision"] = expectedRevision,
                        ["actualRevision"] = actualRevision,
                        ["documentId"] = actualDocumentId
                    });
            }
        }

        private static JToken DispatchAndPresent(string method, JObject p)
        {
            JToken result = Dispatch(method, p);
            if (IsMutation(method, p)) PresentMutation(method, p, result);
            return result;
        }

        private static void PresentMutation(string method, JObject p, JToken result)
        {
            string visualMode = (string)p["visualMode"] ?? "batch";
            if (string.Equals(visualMode, "silent", StringComparison.OrdinalIgnoreCase)) return;
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor editor = doc.Editor;
            try { editor.Regen(); } catch { }
            try { AcApp.UpdateScreen(); } catch { }
            List<ObjectId> ids = ResultObjectIds(doc.Database, result);
            if (((bool?)p["highlightResults"] ?? string.Equals(visualMode, "step", StringComparison.OrdinalIgnoreCase)) && ids.Count > 0)
            {
                try { editor.SetImpliedSelection(ids.Take(250).ToArray()); } catch { }
            }
            if ((bool?)p["zoomToResult"] == true && ids.Count > 0)
            {
                try { ZoomToObjectIds(editor, doc.Database, ids, 0.08); } catch { }
            }
            long revision = CadEventJournal.GetRevision(doc);
            if (result is JObject resultObject)
            {
                resultObject["documentId"] = doc.GetHashCode().ToString("X", CultureInfo.InvariantCulture);
                resultObject["revision"] = revision;
            }
            editor.WriteMessage("\n[CAD MCP] Completed " + method + "; revision " + revision + ".");
            CadEventJournal.RecordPresentation(doc, "completed", new JObject
            {
                ["method"] = method,
                ["visualMode"] = visualMode,
                ["resultCount"] = ids.Count
            });
        }

        private static List<ObjectId> ResultObjectIds(Database db, JToken result)
        {
            var handles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (result is JObject value)
            {
                if (value["handle"] != null) handles.Add((string)value["handle"]);
                foreach (JToken handle in value["handles"] as JArray ?? new JArray()) handles.Add((string)handle);
                foreach (JObject item in value["created"] as JArray ?? new JArray())
                    if (item["handle"] != null) handles.Add((string)item["handle"]);
            }
            return IdsFromHandles(db, new JArray(handles), false);
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
                case "textStyle": return WithDocument(true, (d, db, ed) => TextStyleOperation(db, ed, p));
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
                ["mcpVersion"] = "0.1.0",
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
                    ["revision"] = CadEventJournal.GetRevision(doc),
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
                foreach (Document d in docs) items.Add(new JObject { ["id"] = d.GetHashCode().ToString("X"), ["name"] = d.Name, ["path"] = d.Database.Filename, ["active"] = d == docs.MdiActiveDocument, ["readOnly"] = d.IsReadOnly, ["revision"] = CadEventJournal.GetRevision(d) });
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

        private static JObject DocumentResult(Document d) => new JObject { ["id"] = d.GetHashCode().ToString("X"), ["name"] = d.Name, ["path"] = d.Database.Filename, ["readOnly"] = d.IsReadOnly, ["revision"] = CadEventJournal.GetRevision(d) };

        private static JToken Inspect(Database db, Editor ed, JObject p)
        {
            string scope = RequiredString(p, "scope");
            if (scope == "drawing") return DrawingInfo(db);
            if (scope == "entities") return InspectEntities(db, p);
            if (scope == "layers") return ListTable<LayerTable, LayerTableRecord>(db, db.LayerTableId, r => new JObject { ["name"] = r.Name, ["colorIndex"] = r.Color.ColorIndex, ["linetype"] = SymbolName(r.LinetypeObjectId, db), ["on"] = !r.IsOff, ["frozen"] = r.IsFrozen, ["locked"] = r.IsLocked, ["plottable"] = r.IsPlottable });
            if (scope == "linetypes") return ListNames<LinetypeTable, LinetypeTableRecord>(db, db.LinetypeTableId);
            if (scope == "textStyles") return ListTextStyles(db);
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
            Document document = AcApp.DocumentManager.Cast<Document>().FirstOrDefault(item => ReferenceEquals(item.Database, db));
            return new JObject { ["filename"] = db.Filename, ["dwgVersion"] = db.OriginalFileVersion.ToString(), ["units"] = db.Insunits.ToString(), ["currentLayer"] = SymbolName(db.Clayer, db), ["currentSpace"] = db.TileMode ? "model" : "paper", ["extents"] = ext.HasValue ? ExtentsJson(ext.Value) : null, ["lastSaved"] = db.Tdupdate, ["documentId"] = document == null ? null : document.GetHashCode().ToString("X", CultureInfo.InvariantCulture), ["revision"] = CadEventJournal.GetRevision(document) };
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
            Document document = AcApp.DocumentManager.Cast<Document>().FirstOrDefault(item => ReferenceEquals(item.Database, db));
            return new JObject { ["total"] = ids.Count, ["offset"] = offset, ["count"] = entities.Count, ["entities"] = entities, ["documentId"] = document == null ? null : document.GetHashCode().ToString("X", CultureInfo.InvariantCulture), ["revision"] = CadEventJournal.GetRevision(document) };
        }

        private static JToken Draw(Document doc, Database db, Editor ed, JObject p)
        {
            var created = new JArray();
            JArray specifications = p["entities"] as JArray ?? new JArray();
            bool stepMode = string.Equals((string)p["visualMode"], "step", StringComparison.OrdinalIgnoreCase);
            Matrix3d inputToWorld = string.Equals((string)p["coordinateSystem"], "WCS", StringComparison.OrdinalIgnoreCase)
                ? Matrix3d.Identity
                : ed.CurrentUserCoordinateSystem;
            if (!stepMode)
            {
                using (Transaction transaction = db.TransactionManager.StartTransaction())
                {
                    foreach (JObject specification in specifications)
                    {
                        created.Add(AppendDrawEntity(transaction, db, specification, (string)p["space"], inputToWorld, out ObjectId ignored));
                    }
                    transaction.Commit();
                }
            }
            else
            {
                try
                {
                    int index = 0;
                    foreach (JObject specification in specifications)
                    {
                        ObjectId createdId;
                        JObject item;
                        using (Transaction transaction = db.TransactionManager.StartTransaction())
                        {
                            item = AppendDrawEntity(transaction, db, specification, (string)p["space"], inputToWorld, out createdId);
                            transaction.Commit();
                        }
                        created.Add(item);
                        index++;
                        PresentDrawStep(doc, ed, createdId, index, specifications.Count, (string)specification["type"], (bool?)p["highlightResults"] ?? true);
                    }
                }
                catch (System.Exception exception)
                {
                    CleanupCreated(db, created);
                    try { ed.Regen(); AcApp.UpdateScreen(); } catch { }
                    throw new CadMcpException("DRAW_STEP_FAILED", "Step-mode drawing failed and committed entities were removed.", new JObject
                    {
                        ["completedSteps"] = created.Count,
                        ["cause"] = exception.Message
                    });
                }
            }
            return new JObject { ["created"] = created, ["count"] = created.Count, ["visualMode"] = stepMode ? "step" : "batch" };
        }

        private static JObject AppendDrawEntity(Transaction transaction, Database db, JObject specification, string requestedSpace,
            Matrix3d inputToWorld, out ObjectId createdId)
        {
            BlockTable blockTable = (BlockTable)transaction.GetObject(db.BlockTableId, OpenMode.ForRead);
            ObjectId spaceId = ResolveSpace(db, blockTable, requestedSpace);
            var space = (BlockTableRecord)transaction.GetObject(spaceId, OpenMode.ForWrite);
            CadEntity entity;
            if ((string)specification["type"] == "hatch")
            {
                var hatch = new Hatch();
                space.AppendEntity(hatch);
                transaction.AddNewlyCreatedDBObject(hatch, true);
                hatch.SetDatabaseDefaults(db);
                hatch.SetHatchPattern(HatchPatternType.PreDefined, (string)specification["pattern"] ?? "SOLID");
                if (specification["scale"] != null) hatch.PatternScale = (double)specification["scale"];
                if (specification["angle"] != null) hatch.PatternAngle = (double)specification["angle"];
                hatch.Associative = (bool?)specification["associative"] ?? true;
                var boundary = new ObjectIdCollection(IdsFromHandles(db, specification["boundaryHandles"] as JArray).ToArray());
                hatch.AppendLoop(HatchLoopTypes.Default, boundary);
                hatch.EvaluateHatch(true);
                entity = hatch;
            }
            else
            {
                entity = CreateEntity(transaction, db, specification);
                if (!inputToWorld.IsEqualTo(Matrix3d.Identity)) entity.TransformBy(inputToWorld);
                space.AppendEntity(entity);
                transaction.AddNewlyCreatedDBObject(entity, true);
                if (entity is BlockReference blockReference) AppendBlockAttributes(transaction, blockReference, specification["attributes"] as JObject);
                if (entity is Table table) table.GenerateLayout();
            }
            ApplyCommon(entity, specification);
            createdId = entity.ObjectId;
            return new JObject
            {
                ["handle"] = entity.Handle.ToString(),
                ["type"] = entity.GetRXClass().DxfName,
                ["objectId"] = entity.ObjectId.ToString()
            };
        }

        private static void AppendBlockAttributes(Transaction transaction, BlockReference reference, JObject values)
        {
            if (values == null || values.Count == 0) return;
            var definition = (BlockTableRecord)transaction.GetObject(reference.BlockTableRecord, OpenMode.ForRead);
            foreach (ObjectId id in definition)
            {
                AttributeDefinition attributeDefinition = transaction.GetObject(id, OpenMode.ForRead, false) as AttributeDefinition;
                if (attributeDefinition == null || attributeDefinition.Constant) continue;
                string value = (string)values[attributeDefinition.Tag];
                if (value == null) continue;
                var attribute = new AttributeReference();
                attribute.SetAttributeFromBlock(attributeDefinition, reference.BlockTransform);
                attribute.TextString = value;
                reference.AttributeCollection.AppendAttribute(attribute);
                transaction.AddNewlyCreatedDBObject(attribute, true);
            }
        }

        private static void PresentDrawStep(Document document, Editor editor, ObjectId id, int index, int total, string entityType, bool highlight)
        {
            try { editor.Regen(); } catch { }
            try { AcApp.UpdateScreen(); } catch { }
            if (highlight)
            {
                try { editor.SetImpliedSelection(new[] { id }); } catch { }
            }
            editor.WriteMessage("\n[CAD MCP] Draw step " + index + "/" + total + ": " + entityType + ".");
            CadEventJournal.RecordPresentation(document, "step", new JObject
            {
                ["index"] = index,
                ["total"] = total,
                ["entityType"] = entityType,
                ["handle"] = id.Handle.ToString()
            });
        }

        private static void CleanupCreated(Database db, JArray created)
        {
            JArray handles = new JArray(created.OfType<JObject>().Select(item => item["handle"]));
            List<ObjectId> ids = IdsFromHandles(db, handles, false);
            using (Transaction transaction = db.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ids)
                {
                    DBObject value = transaction.GetObject(id, OpenMode.ForWrite, false);
                    if (!value.IsErased) value.Erase();
                }
                transaction.Commit();
            }
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
            if (type == "polyline3d")
            {
                var points = new Point3dCollection();
                foreach (JToken item in (JArray)s["points"]) points.Add(P3(item));
                return new Polyline3d(Poly3dType.SimplePoly, points, (bool?)s["closed"] ?? false);
            }
            if (type == "spline")
            {
                var points = new Point3dCollection();
                foreach (JToken item in (JArray)s["fitPoints"]) points.Add(P3(item));
                int degree = Math.Min((int?)s["degree"] ?? 3, points.Count - 1);
                if (degree < 1) throw new CadMcpException("INVALID_ARGUMENT", "spline degree must be smaller than the number of fit points.");
                return new Spline(points, degree, (double?)s["fitTolerance"] ?? 1e-7);
            }
            if (type == "ray" || type == "xline")
            {
                Point3d basePoint = P3(s["basePoint"]);
                Vector3d direction = P3(s["throughPoint"]) - basePoint;
                if (direction.Length < 1e-9) throw new CadMcpException("INVALID_ARGUMENT", type + " direction cannot be zero.");
                if (type == "ray") return new Ray { BasePoint = basePoint, UnitDir = direction.GetNormal() };
                return new Xline { BasePoint = basePoint, UnitDir = direction.GetNormal() };
            }
            if (type == "ellipse") return new Ellipse(P3(s["center"]), Vector3d.ZAxis, V3(s["majorAxis"]), (double)s["radiusRatio"], (double?)s["startParameter"] ?? 0, (double?)s["endParameter"] ?? Math.PI * 2);
            if (type == "text")
            {
                string value = (string)s["text"] ?? "";
                return new DBText
                {
                    Position = P3(s["position"]),
                    TextString = value,
                    Height = (double)s["height"],
                    Rotation = (double?)s["rotation"] ?? 0,
                    TextStyleId = ResolveTextStyle(db, tr, (string)s["style"], value)
                };
            }
            if (type == "mtext")
            {
                string value = (string)s["text"] ?? "";
                return new MText
                {
                    Location = P3(s["position"]),
                    Contents = value,
                    TextHeight = (double)s["height"],
                    Width = (double)s["width"],
                    Rotation = (double?)s["rotation"] ?? 0,
                    TextStyleId = ResolveTextStyle(db, tr, (string)s["style"], value)
                };
            }
            if (type == "blockReference")
            {
                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                string name = RequiredString(s, "name"); if (!bt.Has(name)) throw new CadMcpException("BLOCK_NOT_FOUND", "Block not found: " + name);
                var br = new BlockReference(P3(s["insertionPoint"]), bt[name]) { Rotation = (double?)s["rotation"] ?? 0 };
                if (s["scale"] != null) { Vector3d scale = V3(s["scale"]); br.ScaleFactors = new Scale3d(scale.X, scale.Y, scale.Z); }
                return br;
            }
            if (type == "leader")
            {
                var leader = new Leader { HasArrowHead = (bool?)s["hasArrowHead"] ?? true };
                foreach (JToken point in (JArray)s["points"]) leader.AppendVertex(P3(point));
                return leader;
            }
            if (type == "table")
            {
                JArray rows = (JArray)s["rows"];
                int rowCount = rows.Count;
                int columnCount = rows.OfType<JArray>().Max(row => row.Count);
                var table = new Table { Position = P3(s["position"]), TableStyle = db.Tablestyle };
                table.SetSize(rowCount, columnCount);
                table.SetRowHeight((double?)s["rowHeight"] ?? 8.0);
                table.SetColumnWidth((double?)s["columnWidth"] ?? 30.0);
                string allText = string.Join("", rows.SelectMany(row => row.Values<string>()));
                ObjectId styleId = ResolveTextStyle(db, tr, (string)s["style"], allText);
                for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
                {
                    JArray row = (JArray)rows[rowIndex];
                    for (int columnIndex = 0; columnIndex < columnCount; columnIndex++)
                    {
                        string text = columnIndex < row.Count ? (string)row[columnIndex] ?? "" : "";
                        table.Cells[rowIndex, columnIndex].TextString = text;
                        table.Cells[rowIndex, columnIndex].TextStyleId = styleId;
                        if (s["textHeight"] != null) table.Cells[rowIndex, columnIndex].TextHeight = (double)s["textHeight"];
                    }
                }
                return table;
            }
            string dimensionText = (string)s["dimensionText"] ?? "";
            ObjectId dimensionStyle = ResolveDimensionStyle(db, tr, (string)s["style"]);
            if (type == "alignedDimension") return new AlignedDimension(P3(s["first"]), P3(s["second"]), P3(s["dimensionLine"]), dimensionText, dimensionStyle);
            if (type == "rotatedDimension") return new RotatedDimension((double)s["rotation"], P3(s["first"]), P3(s["second"]), P3(s["dimensionLine"]), dimensionText, dimensionStyle);
            if (type == "radialDimension") return new RadialDimension(P3(s["center"]), P3(s["chordPoint"]), (double?)s["leaderLength"] ?? 10.0, dimensionText, dimensionStyle);
            if (type == "diametricDimension") return new DiametricDimension(P3(s["chordPoint"]), P3(s["farChordPoint"]), (double?)s["leaderLength"] ?? 10.0, dimensionText, dimensionStyle);
            if (type == "angularDimension2Line") return new LineAngularDimension2(P3(s["line1Start"]), P3(s["line1End"]), P3(s["line2Start"]), P3(s["line2End"]), P3(s["arcPoint"]), dimensionText, dimensionStyle);
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

        private static JToken TextStyleOperation(Database db, Editor ed, JObject p)
        {
            string action = RequiredString(p, "action");
            if (action == "list") return ListTextStyles(db);

            string name = RequiredString(p, "name");
            if (action == "apply")
            {
                JObject target = p["target"] as JObject;
                if (target == null) throw new CadMcpException("INVALID_TARGET", "target is required when applying a text style.");
                List<ObjectId> ids = ResolveTargets(db, target, false);
                int applied = 0, skipped = 0;
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    TextStyleTable table = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
                    if (!table.Has(name)) throw new CadMcpException("TEXT_STYLE_NOT_FOUND", "Text style not found: " + name);
                    ObjectId styleId = table[name];
                    foreach (ObjectId id in ids)
                    {
                        DBObject obj = tr.GetObject(id, OpenMode.ForWrite, false);
                        if (obj is DBText text) { text.TextStyleId = styleId; applied++; }
                        else if (obj is MText mtext) { mtext.TextStyleId = styleId; applied++; }
                        else skipped++;
                    }
                    tr.Commit();
                }
                ed.Regen();
                return new JObject { ["action"] = action, ["name"] = name, ["matched"] = ids.Count, ["applied"] = applied, ["skipped"] = skipped };
            }

            JObject result;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                TextStyleTable table = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
                if (action == "create")
                {
                    if (table.Has(name)) throw new CadMcpException("TEXT_STYLE_EXISTS", "Text style already exists: " + name);
                    table.UpgradeOpen();
                    var record = new TextStyleTableRecord { Name = name };
                    ApplyTextStyleProperties(record, p, true);
                    table.Add(record);
                    tr.AddNewlyCreatedDBObject(record, true);
                    result = TextStyleJson(record, db.Textstyle);
                }
                else
                {
                    if (!table.Has(name)) throw new CadMcpException("TEXT_STYLE_NOT_FOUND", "Text style not found: " + name);
                    ObjectId id = table[name];
                    if (action == "update")
                    {
                        var record = (TextStyleTableRecord)tr.GetObject(id, OpenMode.ForWrite);
                        ApplyTextStyleProperties(record, p, false);
                        result = TextStyleJson(record, db.Textstyle);
                    }
                    else if (action == "setCurrent")
                    {
                        db.Textstyle = id;
                        var record = (TextStyleTableRecord)tr.GetObject(id, OpenMode.ForRead);
                        result = TextStyleJson(record, id);
                    }
                    else throw new CadMcpException("INVALID_ACTION", "Unsupported text style action: " + action);
                }
                tr.Commit();
            }
            result["action"] = action;
            return result;
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
                    if (!table.Has(name)) { table.UpgradeOpen(); var rec = new LayerTableRecord { Name = name }; table.Add(rec); tr.AddNewlyCreatedDBObject(rec, true); ApplyLayer(db, tr, rec, p["properties"] as JObject); }
                }
                else if (action == "setCurrent") { EnsureLayer(table, name); db.Clayer = table[name]; }
                else if (action == "update") { EnsureLayer(table, name); var rec = (LayerTableRecord)tr.GetObject(table[name], OpenMode.ForWrite); ApplyLayer(db, tr, rec, p["properties"] as JObject); }
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
                    BlockTableRecord space = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite); var br = new BlockReference(p["insertionPoint"] == null ? Point3d.Origin : P3(p["insertionPoint"]), bt[name]); br.Rotation = (double?)p["rotation"] ?? 0; if (p["scale"] != null) { Vector3d v = V3(p["scale"]); br.ScaleFactors = new Scale3d(v.X, v.Y, v.Z); } space.AppendEntity(br); tr.AddNewlyCreatedDBObject(br, true); AppendBlockAttributes(tr, br, p["attributes"] as JObject);
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
            if (action == "zoomExtents") ZoomToExtents(ed, doc.Database, 0.05);
            else if (action == "regen")
            {
                ed.Regen();
                AcApp.UpdateScreen();
            }
            else if (action == "zoomWindow")
            {
                JArray window = p["window"] as JArray ?? throw new CadMcpException("INVALID_ARGUMENT", "window is required for zoomWindow.");
                SetViewToExtents(ed, new Extents3d(P3(window[0]), P3(window[1])), 0);
            }
            else if (action == "zoomObjects")
            {
                JArray handles = p["handles"] as JArray ?? throw new CadMcpException("INVALID_ARGUMENT", "handles is required for zoomObjects.");
                ZoomToObjectIds(ed, doc.Database, IdsFromHandles(doc.Database, handles), 0.08);
            }
            else if (action == "pan")
            {
                JArray delta = p["delta"] as JArray ?? throw new CadMcpException("INVALID_ARGUMENT", "delta is required for pan.");
                using (ViewTableRecord view = ed.GetCurrentView())
                {
                    view.CenterPoint = new Point2d(view.CenterPoint.X + (double)delta[0], view.CenterPoint.Y + (double)delta[1]);
                    ed.SetCurrentView(view);
                }
            }
            else if (action == "orbit")
            {
                JArray delta = p["delta"] as JArray ?? throw new CadMcpException("INVALID_ARGUMENT", "delta is required for orbit.");
                using (ViewTableRecord view = ed.GetCurrentView())
                {
                    Vector3d direction = view.ViewDirection.Length < 1e-9 ? Vector3d.ZAxis : view.ViewDirection.GetNormal();
                    direction = direction.TransformBy(Matrix3d.Rotation((double)delta[0], Vector3d.ZAxis, Point3d.Origin));
                    Vector3d right = Vector3d.ZAxis.CrossProduct(direction);
                    if (right.Length < 1e-9) right = Vector3d.XAxis;
                    direction = direction.TransformBy(Matrix3d.Rotation((double)delta[1], right.GetNormal(), Point3d.Origin));
                    view.ViewDirection = direction;
                    ed.SetCurrentView(view);
                }
            }
            else if (action == "setView")
            {
                string viewName = RequiredString(p, "viewName");
                using (Transaction transaction = doc.Database.TransactionManager.StartOpenCloseTransaction())
                {
                    ViewTable table = (ViewTable)transaction.GetObject(doc.Database.ViewTableId, OpenMode.ForRead);
                    if (!table.Has(viewName)) throw new CadMcpException("VIEW_NOT_FOUND", "Named view not found: " + viewName);
                    ViewTableRecord view = (ViewTableRecord)transaction.GetObject(table[viewName], OpenMode.ForRead);
                    ed.SetCurrentView(view);
                }
            }
            else if (action == "capture") throw new CadMcpException("INVALID_CONTEXT", "capture must be dispatched asynchronously.");
            else throw new CadMcpException("INVALID_ACTION", "This view action is not implemented in the in-process endpoint: " + action);
            try { AcApp.UpdateScreen(); } catch { }
            return new JObject { ["action"] = action, ["ok"] = true, ["queued"] = false, ["confirmed"] = true };
        }

        private static void ZoomToExtents(Editor editor, Database database, double margin)
        {
            Extents3d extents;
            try { extents = new Extents3d(database.Extmin, database.Extmax); }
            catch { return; }
            SetViewToExtents(editor, extents, margin);
        }

        private static void ZoomToObjectIds(Editor editor, Database database, IEnumerable<ObjectId> objectIds, double margin)
        {
            Extents3d? combined = null;
            using (Transaction transaction = database.TransactionManager.StartOpenCloseTransaction())
            {
                foreach (ObjectId id in objectIds.Where(value => value.IsValid && !value.IsErased))
                {
                    Entity entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                    if (entity == null) continue;
                    try
                    {
                        Extents3d extents = entity.GeometricExtents;
                        if (!combined.HasValue) combined = extents;
                        else
                        {
                            Extents3d current = combined.Value;
                            current.AddExtents(extents);
                            combined = current;
                        }
                    }
                    catch { }
                }
            }
            if (combined.HasValue) SetViewToExtents(editor, combined.Value, margin);
        }

        private static void SetViewToExtents(Editor editor, Extents3d worldExtents, double margin)
        {
            using (ViewTableRecord view = editor.GetCurrentView())
            {
                Matrix3d worldToDisplay = Matrix3d.PlaneToWorld(view.ViewDirection);
                worldToDisplay = Matrix3d.Displacement(view.Target - Point3d.Origin) * worldToDisplay;
                worldToDisplay = Matrix3d.Rotation(-view.ViewTwist, view.ViewDirection, view.Target) * worldToDisplay;
                worldToDisplay = worldToDisplay.Inverse();
                Extents3d displayExtents = worldExtents;
                displayExtents.TransformBy(worldToDisplay);
                double width = Math.Max(displayExtents.MaxPoint.X - displayExtents.MinPoint.X, 1e-6) * (1 + margin * 2);
                double height = Math.Max(displayExtents.MaxPoint.Y - displayExtents.MinPoint.Y, 1e-6) * (1 + margin * 2);
                double viewRatio = view.Width > 1e-9 && view.Height > 1e-9 ? view.Width / view.Height : 1.0;
                if (width / height > viewRatio) height = width / viewRatio;
                else width = height * viewRatio;
                view.Width = width;
                view.Height = height;
                view.CenterPoint = new Point2d(
                    (displayExtents.MinPoint.X + displayExtents.MaxPoint.X) / 2,
                    (displayExtents.MinPoint.Y + displayExtents.MaxPoint.Y) / 2);
                editor.SetCurrentView(view);
            }
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

        private static List<ObjectId> IdsFromHandles(Database db, JArray handles) => IdsFromHandles(db, handles, true);

        private static List<ObjectId> IdsFromHandles(Database db, JArray handles, bool throwOnMissing)
        {
            var ids = new List<ObjectId>();
            foreach (JToken token in handles ?? new JArray())
            {
                if (!long.TryParse((string)token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long value))
                {
                    if (throwOnMissing) throw new CadMcpException("INVALID_HANDLE", "Invalid entity handle: " + token);
                    continue;
                }
                try
                {
                    ObjectId id = db.GetObjectId(false, new Handle(value), 0);
                    if (id.IsValid && !id.IsErased) ids.Add(id);
                }
                catch
                {
                    if (throwOnMissing) throw new CadMcpException("HANDLE_NOT_FOUND", "Entity handle not found: " + token);
                }
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
            else if (e is Spline spline) { var points = new JArray(); for (int i = 0; i < spline.NumFitPoints; i++) points.Add(PointJson(spline.GetFitPointAt(i))); j["fitPoints"] = points; j["degree"] = spline.Degree; j["closed"] = spline.Closed; }
            else if (e is Ray ray) { j["basePoint"] = PointJson(ray.BasePoint); j["unitDirection"] = new JArray(ray.UnitDir.X, ray.UnitDir.Y, ray.UnitDir.Z); }
            else if (e is Xline xline) { j["basePoint"] = PointJson(xline.BasePoint); j["unitDirection"] = new JArray(xline.UnitDir.X, xline.UnitDir.Y, xline.UnitDir.Z); }
            else if (e is Leader leader) { var points = new JArray(); for (int i = 0; i < leader.NumVertices; i++) points.Add(PointJson(leader.VertexAt(i))); j["points"] = points; j["hasArrowHead"] = leader.HasArrowHead; }
            else if (e is Table table) { var rows = new JArray(); for (int row = 0; row < table.Rows.Count; row++) { var values = new JArray(); for (int column = 0; column < table.Columns.Count; column++) values.Add(table.Cells[row, column].TextString); rows.Add(values); } j["position"] = PointJson(table.Position); j["rows"] = rows; }
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

        private static JToken ListTextStyles(Database db)
        {
            var items = new JArray();
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                TextStyleTable table = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
                foreach (ObjectId id in table)
                {
                    var record = (TextStyleTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    items.Add(TextStyleJson(record, db.Textstyle));
                }
            }
            return new JObject { ["items"] = items, ["count"] = items.Count, ["recommendedCjkStyle"] = ChineseTextStyleName };
        }

        private static JObject TextStyleJson(TextStyleTableRecord record, ObjectId currentId)
        {
            return new JObject
            {
                ["name"] = record.Name,
                ["fileName"] = record.FileName,
                ["bigFontFileName"] = record.BigFontFileName,
                ["xScale"] = record.XScale,
                ["obliqueAngle"] = record.ObliquingAngle,
                ["vertical"] = record.IsVertical,
                ["current"] = record.ObjectId == currentId,
                ["fontAvailable"] = FontAvailable(record.FileName)
            };
        }

        private static bool FontAvailable(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return false;
            if (Path.IsPathRooted(fileName)) return File.Exists(fileName);
            string fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            return File.Exists(Path.Combine(fonts, Path.GetFileName(fileName))) || File.Exists(fileName);
        }

        private static void ApplyTextStyleProperties(TextStyleTableRecord record, JObject p, bool defaultFont)
        {
            if (p["fontFile"] != null) record.FileName = (string)p["fontFile"];
            else if (defaultFont) record.FileName = ChooseChineseFont();
            if (p["bigFontFile"] != null) record.BigFontFileName = (string)p["bigFontFile"];
            if (p["xScale"] != null) record.XScale = (double)p["xScale"];
            if (p["obliqueAngle"] != null) record.ObliquingAngle = (double)p["obliqueAngle"];
            if (p["vertical"] != null) record.IsVertical = (bool)p["vertical"];
        }

        private static ObjectId ResolveTextStyle(Database db, Transaction tr, string requested, string text)
        {
            TextStyleTable table = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
            if (!string.IsNullOrWhiteSpace(requested))
            {
                if (!table.Has(requested)) throw new CadMcpException("TEXT_STYLE_NOT_FOUND", "Text style not found: " + requested);
                return table[requested];
            }
            return ContainsCjk(text) ? EnsureChineseTextStyle(db, tr) : db.Textstyle;
        }

        private static ObjectId ResolveDimensionStyle(Database db, Transaction transaction, string requested)
        {
            if (string.IsNullOrWhiteSpace(requested)) return db.Dimstyle;
            DimStyleTable table = (DimStyleTable)transaction.GetObject(db.DimStyleTableId, OpenMode.ForRead);
            if (!table.Has(requested)) throw new CadMcpException("DIMENSION_STYLE_NOT_FOUND", "Dimension style not found: " + requested);
            return table[requested];
        }

        private static ObjectId EnsureChineseTextStyle(Database db, Transaction tr)
        {
            TextStyleTable table = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
            if (table.Has(ChineseTextStyleName)) return table[ChineseTextStyleName];
            table.UpgradeOpen();
            var record = new TextStyleTableRecord { Name = ChineseTextStyleName, FileName = ChooseChineseFont(), XScale = 1.0 };
            ObjectId id = table.Add(record);
            tr.AddNewlyCreatedDBObject(record, true);
            return id;
        }

        private static string ChooseChineseFont()
        {
            string fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            foreach (string candidate in new[] { "msyh.ttc", "simsun.ttc" })
            {
                if (File.Exists(Path.Combine(fonts, candidate))) return candidate;
            }
            throw new CadMcpException("FONT_NOT_FOUND", "No supported Chinese font was found. Install Microsoft YaHei or SimSun.");
        }

        private static bool ContainsCjk(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            foreach (char ch in value)
            {
                if ((ch >= '\u3400' && ch <= '\u4DBF') ||
                    (ch >= '\u4E00' && ch <= '\u9FFF') ||
                    (ch >= '\uF900' && ch <= '\uFAFF'))
                    return true;
            }
            return false;
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
            if (p["linetypeScale"] != null) e.LinetypeScale = (double)p["linetypeScale"];
            if (p["lineweight"] != null) e.LineWeight = (LineWeight)(int)p["lineweight"];
            if (p["visible"] != null) e.Visible = (bool)p["visible"];
        }

        private static void ApplyLayer(Database db, Transaction transaction, LayerTableRecord r, JObject p)
        {
            if (p == null) return;
            if (p["colorIndex"] != null) r.Color = Color.FromColorIndex(ColorMethod.ByAci, (short)(int)p["colorIndex"]);
            if (p["lineweight"] != null) r.LineWeight = (LineWeight)(int)p["lineweight"];
            if (p["linetype"] != null)
            {
                string name = (string)p["linetype"];
                LinetypeTable table = (LinetypeTable)transaction.GetObject(db.LinetypeTableId, OpenMode.ForRead);
                if (!table.Has(name)) throw new CadMcpException("LINETYPE_NOT_FOUND", "Linetype not found: " + name);
                r.LinetypeObjectId = table[name];
            }
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
