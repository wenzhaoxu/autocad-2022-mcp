using Autodesk.AutoCAD.ApplicationServices.Core;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Newtonsoft.Json.Linq;

namespace AutoCAD2024Mcp
{
    public sealed class Plugin : IExtensionApplication
    {
        private static PipeBridge _bridge;
        private static bool _idleHooked;

        public void Initialize()
        {
            RunSafely("initialize", () =>
            {
                EnsureStarted();
                Write("AutoCAD 2024 MCP bridge 0.1.1 started. Pipe: " + _bridge.PipeName);
            });
        }

        public void Terminate()
        {
            RunSafely("terminate", () =>
            {
                if (_idleHooked)
                {
                    Application.Idle -= OnIdle;
                    _idleHooked = false;
                }
                _bridge?.Dispose();
                _bridge = null;
                CadEventJournal.Terminate();
            });
        }

        private static void OnIdle(object sender, System.EventArgs e) => _bridge?.DrainOnCadThread();

        [CommandMethod("CADMCPSTART", CommandFlags.Session)]
        public void Start()
        {
            RunSafely("start", () =>
            {
                EnsureStarted();
                Write("MCP bridge is running. Pipe: " + _bridge.PipeName);
            });
        }

        private static void EnsureStarted()
        {
            CadEventJournal.Initialize();
            if (_bridge == null) _bridge = new PipeBridge();
            _bridge.Start();
            if (_idleHooked) return;
            Application.Idle += OnIdle;
            _idleHooked = true;
        }

        [CommandMethod("CADMCPSTOP", CommandFlags.Session)]
        public void Stop()
        {
            RunSafely("stop", () =>
            {
                if (_idleHooked)
                {
                    Application.Idle -= OnIdle;
                    _idleHooked = false;
                }
                _bridge?.Stop();
                CadEventJournal.Terminate();
                Write("MCP bridge stopped.");
            });
        }

        [CommandMethod("CADMCPSTATUS", CommandFlags.Session)]
        public void Status() => RunSafely("status", () => Write(_bridge?.IsRunning == true ? "MCP bridge is running. Version 0.1.1." : "MCP bridge is stopped."));

        [CommandMethod("CADMCPSELFTEST")]
        public void SelfTest()
        {
            RunSafely("self-test", () =>
            {
                var document = Application.DocumentManager.MdiActiveDocument;
                if (document == null) throw new System.InvalidOperationException("No active drawing for self-test.");
                var handles = new JArray();
                try
                {
                    JToken drawing = CadDispatcher.Dispatch("draw", new JObject
                    {
                        ["coordinateSystem"] = "WCS",
                        ["entities"] = new JArray
                        {
                            new JObject { ["type"] = "line", ["start"] = new JArray(0, 0, 0), ["end"] = new JArray(10, 0, 0) },
                            new JObject { ["type"] = "spline", ["fitPoints"] = new JArray { new JArray(0, 0, 0), new JArray(5, 3, 0), new JArray(10, 0, 0) }, ["degree"] = 2 },
                            new JObject { ["type"] = "table", ["position"] = new JArray(0, 10, 0), ["rows"] = new JArray { new JArray("Name", "Count"), new JArray("Fan", "2") } }
                        }
                    });
                    foreach (JObject item in drawing["created"] as JArray ?? new JArray()) handles.Add((string)item["handle"]);

                    JToken solid = CadDispatcher.Dispatch("solid", new JObject
                    {
                        ["action"] = "create",
                        ["primitives"] = new JArray
                        {
                            new JObject { ["type"] = "torus", ["center"] = new JArray(25, 0, 0), ["majorRadius"] = 5, ["minorRadius"] = 1 }
                        }
                    });
                    foreach (JObject item in solid["created"] as JArray ?? new JArray()) handles.Add((string)item["handle"]);

                    JToken inspected = CadDispatcher.Dispatch("inspect", new JObject { ["scope"] = "entities", ["handles"] = handles });
                    JObject events = CadEventJournal.Read(new JObject { ["afterSequence"] = 0, ["limit"] = 1000 });
                    if ((int)inspected["count"] != handles.Count || (int)events["count"] == 0)
                        throw new System.InvalidOperationException("Structured drawing or event journal verification failed.");
                    Write("SELFTEST OK: structured drawing, solid creation, inspection, and event journal.");
                }
                finally
                {
                    if (handles.Count > 0)
                    {
                        try
                        {
                            CadDispatcher.Dispatch("edit", new JObject
                            {
                                ["target"] = new JObject { ["handles"] = handles },
                                ["operations"] = new JArray { new JObject { ["op"] = "erase" } },
                                ["allowEmpty"] = true
                            });
                        }
                        catch { }
                    }
                }
            });
        }

        [CommandMethod("CADMCPDIFFERENTIAL")]
        public void CreateDifferential()
        {
            RunSafely("differential-2d", () =>
            {
                var doc = Application.DocumentManager.MdiActiveDocument;
                if (doc == null) return;
                var options = new PromptStringOptions("\nOutput DWG path: ") { AllowSpaces = true };
                PromptResult result = doc.Editor.GetString(options);
                if (result.Status == PromptStatus.OK) DifferentialDrawing.Create(doc, result.StringResult);
            });
        }

        [CommandMethod("CADMCPDIFFERENTIAL3D")]
        public void CreateDifferential3D()
        {
            RunSafely("differential-3d", () =>
            {
                var doc = Application.DocumentManager.MdiActiveDocument;
                if (doc == null) return;
                var options = new PromptStringOptions("\nOutput 3D assembly DWG path: ") { AllowSpaces = true };
                PromptResult result = doc.Editor.GetString(options);
                if (result.Status == PromptStatus.OK) Differential3D.Create(doc, result.StringResult);
            });
        }

        private static void RunSafely(string operation, System.Action action)
        {
            try { action(); }
            catch (Autodesk.AutoCAD.Runtime.Exception ex) { Write("Operation '" + operation + "' failed safely: " + ex.ErrorStatus); }
            catch (System.Exception ex) { Write("Operation '" + operation + "' failed safely: " + ex.Message); }
        }

        private static void Write(string message)
        {
            Editor editor = Application.DocumentManager.MdiActiveDocument?.Editor;
            editor?.WriteMessage("\n[CAD MCP] " + message);
        }
    }
}
