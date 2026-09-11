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
        public int ProcessId { get; }
        public string PipeName { get; }

        public CadInstanceRegistration()
        {
            Process process = Process.GetCurrentProcess();
            ProcessId = process.Id;
            PipeName = "autocad-2024-mcp-" + ProcessId;
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoCAD2024Mcp", "instances");
            Directory.CreateDirectory(root);
            _path = Path.Combine(root, ProcessId + ".json");
        }

        public void Write(string documentName = null, string documentPath = null)
        {
            var payload = new JObject
            {
                ["processId"] = ProcessId,
                ["pipeName"] = PipeName,
                ["product"] = "AutoCAD 2024",
                ["startedAt"] = Process.GetCurrentProcess().StartTime.ToUniversalTime().ToString("O"),
                ["updatedAt"] = DateTime.UtcNow.ToString("O"),
                ["documentName"] = documentName,
                ["documentPath"] = documentPath
            };
            string temporary = _path + ".tmp";
            File.WriteAllText(temporary, payload.ToString(Formatting.None));
            if (File.Exists(_path)) File.Replace(temporary, _path, null);
            else File.Move(temporary, _path);
        }

        public void Dispose()
        {
            try { if (File.Exists(_path)) File.Delete(_path); } catch { }
        }
    }
}
