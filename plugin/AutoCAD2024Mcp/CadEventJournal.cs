using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.ApplicationServices.Core;
using Autodesk.AutoCAD.DatabaseServices;
using Newtonsoft.Json.Linq;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AutoCAD2024Mcp
{
    /// <summary>
    /// Bounded, in-process change journal used to synchronize MCP clients with
    /// changes made by both MCP and interactive AutoCAD commands.
    /// </summary>
    internal static class CadEventJournal
    {
        private const int Capacity = 4096;
        private static readonly object Sync = new object();
        private static readonly LinkedList<JObject> Events = new LinkedList<JObject>();
        private static readonly HashSet<Document> AttachedDocuments = new HashSet<Document>();
        private static readonly Dictionary<string, long> Revisions = new Dictionary<string, long>(StringComparer.Ordinal);
        private static long _sequence;
        private static bool _initialized;

        [ThreadStatic]
        private static string _currentOperationId;

        [ThreadStatic]
        private static string _currentMethod;

        public static string CurrentOperationId => _currentOperationId;

        public static void Initialize()
        {
            if (_initialized) return;
            DocumentCollection documents = AcApp.DocumentManager;
            documents.DocumentCreated += OnDocumentCreated;
            documents.DocumentActivated += OnDocumentActivated;
            documents.DocumentToBeDestroyed += OnDocumentToBeDestroyed;
            foreach (Document document in documents) Attach(document);
            _initialized = true;
            Record("bridge.started", documents.MdiActiveDocument, null, false);
        }

        public static void Terminate()
        {
            if (!_initialized) return;
            DocumentCollection documents = AcApp.DocumentManager;
            documents.DocumentCreated -= OnDocumentCreated;
            documents.DocumentActivated -= OnDocumentActivated;
            documents.DocumentToBeDestroyed -= OnDocumentToBeDestroyed;
            foreach (Document document in AttachedDocuments.ToArray()) Detach(document);
            _initialized = false;
        }

        public static IDisposable BeginOperation(string operationId, string method)
        {
            string previousId = _currentOperationId;
            string previousMethod = _currentMethod;
            _currentOperationId = operationId;
            _currentMethod = method;
            return new Scope(() =>
            {
                _currentOperationId = previousId;
                _currentMethod = previousMethod;
            });
        }

        public static void OperationStarted(string operationId, string method, Document document)
        {
            Record("operation.started", document, new JObject
            {
                ["operationId"] = operationId,
                ["method"] = method
            }, false, operationId, "mcp");
        }

        public static void OperationCompleted(string operationId, string method, Document document, bool succeeded, string errorCode = null, string message = null)
        {
            var details = new JObject
            {
                ["operationId"] = operationId,
                ["method"] = method,
                ["succeeded"] = succeeded
            };
            if (!string.IsNullOrWhiteSpace(errorCode)) details["errorCode"] = errorCode;
            if (!string.IsNullOrWhiteSpace(message)) details["message"] = message;
            Record(succeeded ? "operation.completed" : "operation.failed", document, details, false, operationId, "mcp");
        }

        public static long GetRevision(Document document)
        {
            if (document == null) return 0;
            lock (Sync)
            {
                return Revisions.TryGetValue(DocumentId(document), out long revision) ? revision : 0;
            }
        }

        public static JObject Read(JObject parameters)
        {
            long afterSequence = (long?)parameters["afterSequence"] ?? 0;
            int limit = Math.Max(1, Math.Min((int?)parameters["limit"] ?? 200, 1000));
            string documentId = (string)parameters["documentId"];
            HashSet<string> requestedTypes = null;
            if (parameters["types"] is JArray typeValues && typeValues.Count > 0)
                requestedTypes = new HashSet<string>(typeValues.Values<string>(), StringComparer.OrdinalIgnoreCase);

            lock (Sync)
            {
                long earliest = Events.First == null ? _sequence + 1 : (long)Events.First.Value["sequence"];
                IEnumerable<JObject> selected = Events.Where(item => (long)item["sequence"] > afterSequence);
                if (!string.IsNullOrWhiteSpace(documentId))
                    selected = selected.Where(item => string.Equals((string)item["documentId"], documentId, StringComparison.Ordinal));
                if (requestedTypes != null)
                    selected = selected.Where(item => requestedTypes.Contains((string)item["type"]));
                List<JObject> page = selected.Take(limit + 1).ToList();
                bool hasMore = page.Count > limit;
                JArray items = new JArray(page.Take(limit).Select(item => item.DeepClone()));
                long nextSequence = items.Count == 0 ? _sequence : (long)items[items.Count - 1]["sequence"];
                return new JObject
                {
                    ["events"] = items,
                    ["count"] = items.Count,
                    ["afterSequence"] = afterSequence,
                    ["nextSequence"] = nextSequence,
                    ["latestSequence"] = _sequence,
                    ["hasMore"] = hasMore,
                    ["overflowed"] = afterSequence > 0 && afterSequence < earliest - 1,
                    ["capacity"] = Capacity
                };
            }
        }

        public static void RecordPresentation(Document document, string stage, JObject details = null)
        {
            Record("presentation." + stage, document, details, false);
        }

        private static void OnDocumentCreated(object sender, DocumentCollectionEventArgs args)
        {
            Attach(args.Document);
            Record("document.created", args.Document, null, false);
        }

        private static void OnDocumentActivated(object sender, DocumentCollectionEventArgs args) =>
            Record("document.activated", args.Document, null, false);

        private static void OnDocumentToBeDestroyed(object sender, DocumentCollectionEventArgs args)
        {
            Record("document.closing", args.Document, null, false);
            Detach(args.Document);
        }

        private static void Attach(Document document)
        {
            if (document == null || !AttachedDocuments.Add(document)) return;
            document.CommandWillStart += OnCommandWillStart;
            document.CommandEnded += OnCommandEnded;
            document.CommandFailed += OnCommandFailed;
            document.CommandCancelled += OnCommandCancelled;
            document.Database.ObjectAppended += OnObjectAppended;
            document.Database.ObjectModified += OnObjectModified;
            document.Database.ObjectErased += OnObjectErased;
            lock (Sync)
            {
                string id = DocumentId(document);
                if (!Revisions.ContainsKey(id)) Revisions[id] = 0;
            }
        }

        private static void Detach(Document document)
        {
            if (document == null || !AttachedDocuments.Remove(document)) return;
            try
            {
                document.CommandWillStart -= OnCommandWillStart;
                document.CommandEnded -= OnCommandEnded;
                document.CommandFailed -= OnCommandFailed;
                document.CommandCancelled -= OnCommandCancelled;
                document.Database.ObjectAppended -= OnObjectAppended;
                document.Database.ObjectModified -= OnObjectModified;
                document.Database.ObjectErased -= OnObjectErased;
            }
            catch { }
        }

        private static void OnCommandWillStart(object sender, CommandEventArgs args) =>
            RecordCommand("command.started", sender as Document, args);

        private static void OnCommandEnded(object sender, CommandEventArgs args) =>
            RecordCommand("command.completed", sender as Document, args);

        private static void OnCommandFailed(object sender, CommandEventArgs args) =>
            RecordCommand("command.failed", sender as Document, args);

        private static void OnCommandCancelled(object sender, CommandEventArgs args) =>
            RecordCommand("command.cancelled", sender as Document, args);

        private static void RecordCommand(string type, Document document, CommandEventArgs args)
        {
            Record(type, document, new JObject { ["command"] = args.GlobalCommandName }, false);
        }

        private static void OnObjectAppended(object sender, ObjectEventArgs args) => RecordObject("entity.appended", args.DBObject, false);

        private static void OnObjectModified(object sender, ObjectEventArgs args) => RecordObject("entity.modified", args.DBObject, false);

        private static void OnObjectErased(object sender, ObjectErasedEventArgs args) => RecordObject("entity.erased", args.DBObject, args.Erased);

        private static void RecordObject(string type, DBObject value, bool erased)
        {
            if (!(value is Entity) && !(value is SymbolTableRecord) && !(value is Layout)) return;
            Document document = FindDocument(value.Database);
            var details = new JObject
            {
                ["handle"] = SafeHandle(value),
                ["objectType"] = SafeType(value),
                ["erased"] = erased
            };
            Record(type, document, details, true);
        }

        private static Document FindDocument(Database database)
        {
            if (database == null) return null;
            foreach (Document document in AcApp.DocumentManager)
                if (ReferenceEquals(document.Database, database)) return document;
            return null;
        }

        private static void Record(string type, Document document, JObject details, bool incrementsRevision, string operationId = null, string source = null)
        {
            lock (Sync)
            {
                long revision = 0;
                string documentId = document == null ? null : DocumentId(document);
                if (documentId != null)
                {
                    Revisions.TryGetValue(documentId, out revision);
                    if (incrementsRevision) Revisions[documentId] = revision = revision + 1;
                }
                string correlatedOperation = operationId ?? _currentOperationId;
                var item = new JObject
                {
                    ["sequence"] = ++_sequence,
                    ["revision"] = revision,
                    ["timestamp"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    ["type"] = type,
                    ["source"] = source ?? (string.IsNullOrWhiteSpace(correlatedOperation) ? "autocad" : "mcp"),
                    ["operationId"] = correlatedOperation,
                    ["method"] = _currentMethod,
                    ["documentId"] = documentId,
                    ["documentName"] = SafeDocumentName(document),
                    ["documentPath"] = SafeDocumentPath(document),
                    ["details"] = details
                };
                Events.AddLast(item);
                while (Events.Count > Capacity) Events.RemoveFirst();
            }
        }

        private static string DocumentId(Document document) => document.GetHashCode().ToString("X", CultureInfo.InvariantCulture);

        private static string SafeDocumentName(Document document)
        {
            try { return document?.Name; } catch { return null; }
        }

        private static string SafeDocumentPath(Document document)
        {
            try { return document?.Database?.Filename; } catch { return null; }
        }

        private static string SafeHandle(DBObject value)
        {
            try { return value.Handle.ToString(); } catch { return null; }
        }

        private static string SafeType(DBObject value)
        {
            try { return value.GetRXClass().DxfName; } catch { return value.GetType().Name; }
        }

        private sealed class Scope : IDisposable
        {
            private Action _dispose;
            public Scope(Action dispose) => _dispose = dispose;
            public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
        }
    }
}
