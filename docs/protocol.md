# 管道协议

请求：

```json
{"id":"uuid","method":"inspect","params":{"scope":"entities","filter":{"layer":"WALL"}}}
```

成功响应：

```json
{"id":"uuid","ok":true,"result":{"total":2,"entities":[]}}
```

失败响应：

```json
{"id":"uuid","ok":false,"error":{"code":"NO_ACTIVE_DOCUMENT","message":"No active AutoCAD drawing."}}
```

插件方法与 MCP 工具保持一一映射。每个 AutoCAD 进程注册独立的 `autocad-2024-mcp-{PID}` 管道；客户端通过 `cad_instances` 发现实例并用 `cad_connect` 绑定目标。`status` 和读取型 `inspect` 不持有写锁，普通数据库修改使用活动文档锁和事务。保存、另存及 DWG/DXF 输出由 AutoCAD 原生命令队列执行，并通过命令结束事件返回结果。
