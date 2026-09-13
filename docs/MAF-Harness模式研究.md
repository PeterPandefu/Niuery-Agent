# MAF Harness 模式研究

> 研究对象：Microsoft Agent Framework（MAF）的 Agent Harness / Harness Agent。官方资料通常称为 “Agent Harness”，而不是单独的 “MAF Harness”。资料检索时间：2026-09-13。

## 结论

MAF Harness 默认提供两种 AgentMode：`plan`（计划模式）和 `execute`（执行模式）。官方文档明确写作 “AgentModeProvider supplies `plan` and `execute` modes by default”。因此，用户所说的“计划模式”对应内置的 `plan`；“目标模式”（`goal`/`target`）不是默认内置模式。

模式数量不是硬编码上限。`AgentModeProvider` 允许通过配置提供自定义模式名称和说明，所以可以自行增加名为 `goal`、`target` 或其他名称的模式；这属于应用配置，不是 MAF Harness 的默认模式。

## 两种默认模式

| 模式 | 官方语义 |
| --- | --- |
| `plan` | 交互式规划：分析需求、创建 todos、提出澄清问题、展示计划，并在切换模式前征得用户同意。 |
| `execute` | 自主执行：按计划推进工作，在细节有歧义时作合理决定，并完成 todos。 |

模式通过 `mode_get` / `mode_set` 工具读取和切换。计划到执行的确认属于模式指令层面的行为，而不是工具审批请求。Harness 默认同时启用 `TodoProvider` 和 `AgentModeProvider`；也可以关闭默认模式提供器。

## 官方配置示例

Python Harness（默认启用计划/执行模式）：

```python
from agent_framework import create_harness_agent

agent = create_harness_agent(client=client)
```

关闭默认模式：

```python
agent = create_harness_agent(client=client, disable_mode=True)
```

自定义模式（示意）：

```python
from agent_framework import AgentModeProvider, create_harness_agent

mode_provider = AgentModeProvider(
    default_mode="goal",
    mode_instructions={
        "goal": "围绕目标定义成功标准和下一步行动。",
        "execute": "自主执行已确认的行动。",
    },
)
agent = create_harness_agent(client=client, mode_provider=mode_provider)
```

上例中的 `goal` 由应用定义；如果未传入 `mode_instructions`，提供器才使用默认的 `plan` 与 `execute`。

## 一手资料

1. [Microsoft Learn：Planning and Todos](https://learn.microsoft.com/en-us/agent-framework/agents/planning-and-todos) —— 明确说明默认模式为 `plan` 和 `execute`，定义两者语义，给出 Python/.NET 配置及 Harness 集成；还说明可用 `mode_instructions` 或 `AgentModeProviderOptions.Modes` 自定义模式。
2. [Microsoft Learn：Agent Harness](https://learn.microsoft.com/en-us/agent-framework/concepts/harness) —— Harness 能力矩阵列出默认启用的 “Plan and execute modes”。
3. [Microsoft Agent Framework Python 源码：`_harness/_mode.py`](https://github.com/microsoft/agent-framework/blob/main/python/packages/core/agent_framework/_harness/_mode.py) —— `DEFAULT_MODE_MAP` 仅包含 `plan` 与 `execute`；`AgentModeProvider` 注释说明默认两种模式，并允许传入 `mode_instructions`。
4. [Microsoft Agent Framework Python Harness 样例](https://github.com/microsoft/agent-framework/tree/main/python/samples/02-agents/harness) —— 样例说明两阶段工作流（Plan 交互式、Execute 自主），并展示 `disable_mode=True` 可跳过该工作流。
5. [Microsoft 博客：Meet your Agent Harness and Claw](https://devblogs.microsoft.com/agent-framework/meet-your-agent-harness-and-claw/) —— 介绍 Harness 组合 `TodoProvider` 与 `AgentModeProvider`，并展示 `/mode plan`、`/mode execute` 的控制台切换方式。

## 名称辨析

“MAF harness”也可能被搜索引擎解释为汽车的 MAF（Mass Air Flow，空气流量计）线束。本文讨论的是 Microsoft Agent Framework 的 Harness。对于该软件组件，官方资料没有名为“目标模式”的默认模式；若需求需要目标驱动阶段，应通过自定义 `AgentModeProvider` 模式实现。
