using System;
using System.Diagnostics;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AutoCAD2024Mcp
{
    internal sealed class CadInstanceRegistration : IDisposable
    {
        private readonly string _path;
        private readonly string _startedAt;
        private readonly object _sync = new object();
        private string _lastDocumentName;
        private string _lastDocumentPath;
        private DateTime _lastWriteUtc = DateTime.MinValue;
        private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);
        public int ProcessId { get; }
        public string PipeName { get; }

        public CadInstanceRegistration()
        {
            Process process = Process.GetCurrentProcess();
            ProcessId = process.Id;
            _startedAt = process.StartTime.ToUniversalTime().ToString("O");
            process.Dispose();
            PipeName = "autocad-2024-mcp-" + ProcessId;
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoCAD2024Mcp", "instances");
            Directory.CreateDirectory(root);
            _path = Path.Combine(root, ProcessId + ".json");
        }

        public void Write(string documentName = null, string documentPath = null)
        {
            lock (_sync)
            {
                DateTime now = DateTime.UtcNow;
                bool unchanged = string.Equals(documentName, _lastDocumentName, StringComparison.Ordinal) &&
                    string.Equals(documentPath, _lastDocumentPath, StringComparison.Ordinal);
                if (unchanged && now - _lastWriteUtc < HeartbeatInterval) return;

                var payload = new JObject
                {
                    ["processId"] = ProcessId,
                    ["pipeName"] = PipeName,
                    ["product"] = "AutoCAD 2024",
                    ["startedAt"] = _startedAt,
                    ["updatedAt"] = now.ToString("O"),
                    ["documentName"] = documentName,
                    ["documentPath"] = documentPath
                };
                string temporary = _path + ".tmp";
                File.WriteAllText(temporary, payload.ToString(Formatting.None));
                if (File.Exists(_path)) File.Replace(temporary, _path, null);
                else File.Move(temporary, _path);
                _lastDocumentName = documentName;
                _lastDocumentPath = documentPath;
                _lastWriteUtc = now;
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                try { if (File.Exists(_path)) File.Delete(_path); } catch { }
            }
        }
    }
}
