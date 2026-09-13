# Niuery Agent

使用 C#、Microsoft Agent Framework 和 Electron 构建的桌面编码 Agent 项目。目标为多模型、企业集成和自定义执行策略。

当前处于第 1 阶段：架构文档和可运行的 MAF 技术验收程序。桌面客户端、编码工具和云端服务尚未实现。不得将离线测试结果解释为真实模型或完整产品验收通过。

## 环境

- .NET SDK 10.0.203（同特性带最新补丁可用）。
- PowerShell 7。
- Node.js 24 已用于环境检查；桌面端在第 2 阶段建立。

## 构建与离线测试

```powershell
dotnet restore Niuery.Agent.slnx --locked-mode
dotnet build Niuery.Agent.slnx --no-restore
dotnet test Niuery.Agent.slnx --no-build
```

测试使用明确标注的脚本模型客户端，验证真实 MAF Harness 的工具循环、流式输出、审批、取消与会话序列化。脚本模型仅存在于测试项目中，运行时没有模拟回退。

## 真实模型验收

参照 `config/providers.example.json` 创建未跟踪的 `config/providers.local.json`。填写实际 Base URL、模型名和 API Key。API Key 仅保存在本机配置文件中，不要提交到仓库或发送到聊天。两个提供商编号示例为 `compatible` 和 `local`。

Ollama 无需 API Key，需自行启动并准备支持工具调用的模型。本项目不会自动下载模型或猜测远程服务。

```powershell
pwsh -File scripts/verify-stage1.ps1 -ProviderId compatible
pwsh -File scripts/verify-stage1.ps1 -ProviderId local
```

也可单独运行探针：

```powershell
dotnet run --project src/Niuery.Agent.Diagnostics -- probe config/providers.local.json compatible
```

探针要求模型调用本地无副作用验证工具，取得随机字符串并以流式响应返回；随后序列化、恢复会话，再确认模型能记住字符串且没有重复调用工具。只发送合成验证消息，不读取和上传仓库内容。Ctrl+C 取消，整体时限由配置控制。标准输出返回结构化结果，标准错误返回中文错误；不输出模型原文、原始异常或凭证。

退出码：0 为真实探针通过；1 为调用或断言失败；2 为配置缺失或不合法；3 为取消或超时。阶段完整验收需要同时查看离线测试和真实探针结果。

## 文档

- [首版需求与阶段门禁](docs/01-首版需求.md)
- [架构决策](docs/02-架构决策.md)
- [通信协议草案](docs/03-通信协议.md)
- [阶段验收记录](docs/验收记录.md)

## 实施顺序

1. 架构与 MAF 验证。
2. Electron、本地 Worker、事件流和 SQLite。
3. 编码工具、审批、差异和失败测试修复闭环。
4. 权限、恢复、超时、上下文与凭证管理。
5. OpenAI 兼容服务与 Ollama 的真实任务评测。

所有阶段分别记录证据和待完成项，前一阶段验收通过后再推进下一阶段。
