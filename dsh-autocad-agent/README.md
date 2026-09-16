# DeepSeek Harness AutoCAD 工程智能体

此包通过官方 `@deepseek-ai/dsh-mcp-client` 将 AutoCAD 2024 MCP 注册为 DeepSeek Harness 插件。工具在 Harness 中使用 `mcp__autocad_2024__*` 命名空间。

## 工作方式

- 智能体先读取图纸状态与工程标准，再生成计划。
- 优先用 `cad_workflow` 预检有界变更集，确认后再执行。
- 执行时检查目标文档、参数模式和幂等键；失败时执行补偿撤销。
- 写入后必须查询或检查结果，再保存或导出。
- MCP 审计日志会递归脱敏，不记录令牌、密码等敏感值。

## 准备

1. 安装并启动 AutoCAD 2024，加载本仓库的 AutoCAD MCP 插件。
2. 在仓库根目录构建 MCP 服务：

```powershell
npm install
npm run build
```

3. 设置绝对路径，避免 Harness 从其他目录启动时解析到错误入口：

```powershell
$env:AUTOCAD_MCP_ROOT = 'D:\CAD'
$env:AUTOCAD_MCP_ENTRY = 'D:\CAD\dist\src\index.js'
$env:AUTOCAD_MCP_AUDIT_LOG = 'D:\CAD\output\autocad-mcp-audit.jsonl'
```

## 安装和运行

```powershell
dsh plugin --profile autocad-agent add file:D:/CAD/dsh-autocad-agent
dsh --profile autocad-agent web
```

DeepSeek Harness 当前仍是开发者预览版本，部署前应固定 Harness 与插件依赖版本，并在非生产图纸上完成回归验证。
