using Autodesk.AutoCAD.ApplicationServices.Core;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;

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
                Write("AutoCAD 2024 MCP bridge 0.0.3 started. Pipe: " + _bridge.PipeName);
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
                Write("MCP bridge stopped.");
            });
        }

        [CommandMethod("CADMCPSTATUS", CommandFlags.Session)]
        public void Status() => RunSafely("status", () => Write(_bridge?.IsRunning == true ? "MCP bridge is running. Version 0.0.3." : "MCP bridge is stopped."));

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
