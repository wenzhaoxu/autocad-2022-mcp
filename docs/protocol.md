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

`status`、`inspect drawing` 和 `inspect entities` 返回活动图纸的 `documentId` 与递增 `revision`。写请求可携带：

```json
{"expectedDocumentId":"2A5F0C1","expectedRevision":18,"visualMode":"step"}
```

活动文档或修订号不匹配时分别返回 `DOCUMENT_CONFLICT`、`REVISION_CONFLICT`。这是一种乐观并发保护，用于阻止 MCP 覆盖用户刚刚在 AutoCAD 中完成的修改。

事件请求不会进入 CAD 写操作队列，因此可以在长时间保存、导出或原生命令运行期间读取：

```json
{"id":"uuid","method":"events","params":{"afterSequence":120,"limit":200}}
```

响应中的 `events` 包含 `sequence`、`revision`、`timestamp`、`type`、`source`、`operationId`、文档信息和事件细节。客户端必须保存 `nextSequence` 并用于下一次读取。环形缓冲区容量为 4096；`overflowed=true` 时应重新检查当前图纸。

管道帧固定使用 UTF-8 JSON，以换行符结束。文字内容不得先转换到系统 ANSI 代码页，也不得把中文预先替换为 `?` 或手写 DXF 转义。`draw` 的 `TEXT`/`MTEXT` 可传入 `style`；省略样式且内容含 CJK 字符时，插件自动选择 Unicode 字体样式。`textStyle` 方法用于列出、创建、更新、设为当前和批量应用文字样式。字体文件名指向运行 AutoCAD 的 Windows 字体环境，不通过协议上传字体文件。
