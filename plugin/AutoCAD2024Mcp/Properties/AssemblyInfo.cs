using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Autodesk.AutoCAD.Runtime;

[assembly: AssemblyTitle("AutoCAD 2024 MCP Bridge")]
[assembly: AssemblyDescription("Local named-pipe bridge for AutoCAD 2024 MCP")]
[assembly: AssemblyVersion("0.0.3.0")]
[assembly: TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]
[assembly: ComVisible(false)]
[assembly: ExtensionApplication(typeof(AutoCAD2024Mcp.Plugin))]
[assembly: CommandClass(typeof(AutoCAD2024Mcp.Plugin))]
