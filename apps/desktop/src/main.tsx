import React, { useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import { createRoot } from "react-dom/client";
import ReactMarkdown from "react-markdown";
import remarkGfm from "remark-gfm";
import mermaid from "mermaid";
import {
  Activity,
  AlertTriangle,
  ArrowUp,
  Check,
  CheckCircle2,
  ChevronDown,
  ChevronRight,
  CircleStop,
  Eye,
  EyeOff,
  FileCode2,
  FileDiff,
  FolderOpen,
  GitBranch,
  History,
  Menu,
  MessageCircle,
  PanelRight,
  Plus,
  RotateCcw,
  Settings2,
  ShieldCheck,
  SlidersHorizontal,
  SquareTerminal,
  Trash2,
  X,
  XCircle,
  Zap,
} from "lucide-react";
import "./style.css";
import type { Method } from "./protocol.generated";

type Run = {
  id: string;
  taskId: string;
  parentId?: string | null;
  workspace: string;
  prompt: string;
  provider: string;
  status: string;
  created: string;
  kind?: "project" | "chat";
  taskTitle?: string;
};
type AgentMode = "plan" | "execute";
type Project = { id: string; path: string; name: string; isGit?: boolean };
type Event = {
  runId: string;
  sequence: number;
  type: string;
  timestamp: string;
  payload: Record<string, any>;
};
type Provider = {
  id: string;
  model: string;
  models?: string[];
  kind: string;
  baseUrl?: string;
  apiKey?: string;
  supportsTools?: boolean;
  supportsStreaming?: boolean;
  timeoutSeconds?: number;
  enabled?: boolean;
};
type Artifact = {
  artifactId: string;
  path: string;
  before: string;
  after: string;
  message?: string;
  hash: string;
  workspace: string;
  current?: string;
  currentHash?: string;
  changedSinceArtifact?: boolean;
};
type Approval = {
  approvalId: string;
  digest: string;
  name: string;
  arguments?: any;
  message?: string;
};
declare global {
  interface Window {
    agent: {
      request: (method: Method, payload?: object) => Promise<any>;
      chooseWorkspace: () => Promise<string | null>;
      onEvent: (cb: (e: Event) => void) => () => void;
      onStatus: (cb: (s: string) => void) => () => void;
    };
  }
}

const statusText: Record<string, string> = {
  running: "执行中",
  completed: "已完成",
  failed: "执行失败",
  cancelled: "已取消",
  interrupted: "已中断",
};
const statusIcon: Record<string, React.ReactNode> = {
  running: <Zap size={13} />,
  completed: <CheckCircle2 size={13} />,
  failed: <XCircle size={13} />,
  cancelled: <CircleStop size={13} />,
  interrupted: <AlertTriangle size={13} />,
};
const eventText: Record<string, string> = {
  "run.started": "开始执行",
  "run.completed": "任务完成",
  "run.failed": "执行失败",
  "run.cancelled": "已取消执行",
  "run.interrupted": "执行被中断",
  "approval.resolved": "审批已处理",
  "command.completed": "命令执行完成",
  "command.failed": "命令执行失败",
  "mode.changed": "运行模式已切换",
  "session.restored": "已恢复任务会话",
  "session.restore.failed": "任务会话恢复失败，已创建新会话",
  "session.save.failed": "任务状态保存失败",
};

function timelineEventsForRun(events: Event[], runId: string) {
  return events.filter((event) =>
    event.runId === runId &&
    event.type !== "message.delta" &&
    event.type !== "approval.requested" &&
    event.type !== "approval.resolved",
  );
}

function timelineEventLabel(event: Event) {
  return event.payload.message || eventText[event.type] || event.type;
}

function eventOutput(event: Event) {
  if (event.type === "command.completed") {
    const output = event.payload.output;
    return typeof output === "string" && output ? output : null;
  }
  if (event.type === "tool.completed" && event.payload.name === "RunCommand") {
    const result = event.payload.result;
    return result && typeof result.output === "string" && result.output
      ? `退出码：${result.exitCode}\n${result.output}`
      : result && typeof result.exitCode === "number"
        ? `退出码：${result.exitCode}`
        : null;
  }
  return null;
}

const thinkingPhrases = [
  "脑瓜飞转",
  "火花四溅",
  "哒哒狂奔",
  "CPU烧了、小跑带风",
  "开足马力",
  "火箭发射",
  "眼冒金星",
  "脑筋急刹",
  "逻辑起飞",
  "撒丫子冲",
  "鞋底冒烟",
  "原地起飞",
  "意念超车",
  "嗖的一下",
];

function randomThinkingPhrase(current?: string) {
  const candidates = current
    ? thinkingPhrases.filter((phrase) => phrase !== current)
    : thinkingPhrases;
  return candidates[Math.floor(Math.random() * candidates.length)];
}

function ThinkingIndicator() {
  const [thinking, setThinking] = useState(() => ({
    phrase: randomThinkingPhrase(),
    dots: 0,
  }));

  useEffect(() => {
    const timer = window.setInterval(() => {
      setThinking((current) => current.dots >= 3
        ? { phrase: randomThinkingPhrase(current.phrase), dots: 0 }
        : { ...current, dots: current.dots + 1 });
    }, 1000);
    return () => window.clearInterval(timer);
  }, []);

  return (
    <div className="thinking">
      <span className="thinking-dots"><i /><i /><i /></span>
      <span aria-live="polite">{thinking.phrase}{".".repeat(thinking.dots)}</span>
    </div>
  );
}

let mermaidDiagramCounter = 0;

function MermaidDiagram({ chart }: { chart: string }) {
  const containerRef = useRef<HTMLDivElement | null>(null);
  const diagramId = useMemo(() => `mermaid-diagram-${++mermaidDiagramCounter}`, []);
  const [error, setError] = useState("");

  useEffect(() => {
    let cancelled = false;
    setError("");
    mermaid.initialize({
      startOnLoad: false,
      securityLevel: "strict",
      theme: "base",
    });
    mermaid
      .render(diagramId, chart)
      .then(({ svg, bindFunctions }) => {
        if (cancelled || !containerRef.current) return;
        containerRef.current.innerHTML = svg;
        bindFunctions?.(containerRef.current);
      })
      .catch((reason: unknown) => {
        if (cancelled) return;
        if (containerRef.current) containerRef.current.innerHTML = "";
        setError(reason instanceof Error ? reason.message : "图表语法无法解析。");
      });
    return () => {
      cancelled = true;
      if (containerRef.current) containerRef.current.innerHTML = "";
    };
  }, [chart, diagramId]);

  return (
    <div className="mermaid-block">
      <div ref={containerRef} className="mermaid-diagram" role="img" aria-label="Mermaid 图表" />
      {error && (
        <details className="mermaid-error">
          <summary>Mermaid 图表渲染失败</summary>
          <p>{error}</p>
          <pre><code>{chart}</code></pre>
        </details>
      )}
    </div>
  );
}

type MarkdownCodeProps = React.ComponentPropsWithoutRef<"code"> & { node?: unknown };

function MarkdownCode({ className, children, ...props }: MarkdownCodeProps) {
  const language = /language-(\w+)/.exec(className || "")?.[1].toLowerCase();
  if (language === "mermaid") {
    return <MermaidDiagram chart={String(children).replace(/\n$/, "")} />;
  }
  return <code className={className} {...props}>{children}</code>;
}

function MarkdownPre({ children }: React.ComponentPropsWithoutRef<"pre">) {
  const child = React.Children.toArray(children)[0];
  if (React.isValidElement(child) && child.type === MermaidDiagram) return child;
  return <pre>{children}</pre>;
}

function MarkdownAnswer({ content }: { content: string }) {
  return (
    <ReactMarkdown remarkPlugins={[remarkGfm]} components={{ code: MarkdownCode, pre: MarkdownPre }}>
      {content}
    </ReactMarkdown>
  );
}

function App() {
  const [runs, setRuns] = useState<Run[]>([]),
    [providers, setProviders] = useState<Provider[]>([]),
    [selected, setSelected] = useState<string | null>(null),
    [events, setEvents] = useState<Event[]>([]),
    [artifacts, setArtifacts] = useState<Artifact[]>([]);
  const [workspace, setWorkspace] = useState(
      localStorage.getItem("workspace") || "",
    ),
    [provider, setProvider] = useState(""),
    [prompt, setPrompt] = useState(""),
    [error, setError] = useState(""),
    [busy, setBusy] = useState(false);
  const [projects, setProjects] = useState<Project[]>(() => {
    try { return JSON.parse(localStorage.getItem("projects") || "[]"); } catch { return []; }
  });
  const [activeProjectId, setActiveProjectId] = useState(() => localStorage.getItem("activeProjectId") || "");
  const [composeKind, setComposeKind] = useState<"project" | "chat">("project");
  const [workMode, setWorkMode] = useState<AgentMode>("plan");
  const [contextMenu, setContextMenu] = useState<{ taskId: string; x: number; y: number } | null>(null);
  const [projectToRemove, setProjectToRemove] = useState<Project | null>(null);
  const [keyframeHoverIndex, setKeyframeHoverIndex] = useState<number | null>(null);
  const [activeTurnId, setActiveTurnId] = useState<string | null>(null);
  const conversationRef = useRef<HTMLElement | null>(null);
  const stickConversationToBottom = useRef(false);
  const [settings, setSettings] = useState(false),
    [rightOpen, setRightOpen] = useState(true),
    [rightTab, setRightTab] = useState<"diff" | "activity">("diff"),
    [expanded, setExpanded] = useState<Record<string, boolean>>({}),
    [timelineExpanded, setTimelineExpanded] = useState<Record<string, boolean>>({}),
    [customProviders, setCustomProviders] = useState<Provider[]>([]),
    [settingsProviderId, setSettingsProviderId] = useState(""),
    [selectedModel, setSelectedModel] = useState(""),
    [modelInput, setModelInput] = useState(""),
    [showApiKey, setShowApiKey] = useState(false),
    [syncOpen, setSyncOpen] = useState(false),
    [syncLoading, setSyncLoading] = useState(false),
    [syncModelsList, setSyncModelsList] = useState<string[]>([]),
    [syncSelectedModels, setSyncSelectedModels] = useState<string[]>([]),
    [providerToRemove, setProviderToRemove] = useState<Provider | null>(null),
    [testingProviderId, setTestingProviderId] = useState<string | null>(null),
    [connectionResult, setConnectionResult] = useState<{ providerId: string; ok: boolean; message: string } | null>(null);
  const allProviders = [...providers, ...customProviders];
  useEffect(() => { localStorage.setItem("projects", JSON.stringify(projects)); }, [projects]);
  useEffect(() => { localStorage.setItem("activeProjectId", activeProjectId); }, [activeProjectId]);
  useEffect(() => {
    if (projects.length === 0 && workspace) {
      const name = workspace.split(/[\\/]/).filter(Boolean).pop() || workspace;
      const item = { id: workspace, path: workspace, name };
      setProjects([item]); setActiveProjectId(item.id);
    }
  }, []);
  const refresh = () =>
    window.agent
      .request("history.list", { limit: 1000, offset: 0 })
      .then(setRuns)
      .catch((e: Error) => setError(e.message));
  const refreshDiff = (runId: string) =>
    window.agent
      .request("workspace.diff", { runId })
      .then((items: Artifact[]) => setArtifacts(items))
      .catch((e: Error) => setError(e.message));
  useEffect(() => {
    window.agent
      .request("hello")
      .then(() => window.agent.request("providers.list"))
      .then((items: Provider[]) => {
        setProviders(items);
        const first = items.find((p) => p.enabled !== false);
        setProvider(first?.id || "");
        setSelectedModel(first?.models?.[0] || first?.model || "");
        setSettingsProviderId(items[0]?.id || "");
        return refresh();
      })
      .catch((e: Error) => setError(e.message));
    return window.agent.onStatus(setError);
  }, []);
  useEffect(() => {
    if (!selected) {
      setEvents([]);
      setArtifacts([]);
      return;
    }
    let alive = true;
    setEvents([]);
    const selectedRun = runs.find((run) => run.id === selected);
    const taskRunIds = selectedRun ? runs.filter((run) => run.taskId === selectedRun.taskId).map((run) => run.id) : [selected];
    Promise.all(taskRunIds.map((runId) => window.agent.request("events.list", { runId, limit: 5000 }) as Promise<Event[]>))
      .then((lists) => {
        if (!alive) return;
        const order = new Map(taskRunIds.map((runId, index) => [runId, index]));
        setEvents(lists.flat().sort((a, b) => (order.get(a.runId) || 0) - (order.get(b.runId) || 0) || a.sequence - b.sequence));
      })
      .catch((e: Error) => setError(e.message));
    refreshDiff(selected);
    const off = window.agent.onEvent((event) => {
      if (taskRunIds.includes(event.runId))
        setEvents((old) =>
          old.some((x) => x.runId === event.runId && x.sequence === event.sequence)
            ? old
            : [...old, event].sort((a, b) => a.sequence - b.sequence),
        );
      if (event.runId === selected && event.type === "artifact.created") refreshDiff(selected);
      if (event.type.startsWith("run.")) refresh();
    });
    return () => {
      alive = false;
      off();
    };
  }, [selected, runs.length]);
  const current = runs.find((r) => r.id === selected),
    running = runs.some((r) => r.status === "running"),
    canContinue = !!current && (current.status === "completed" || current.status === "interrupted"),
    projectName =
      workspace.split(/[\\/]/).filter(Boolean).pop() || "未选择项目";
  useEffect(() => {
    if (!current) {
      setWorkMode("plan");
      return;
    }
    window.agent.request("mode.get", { taskId: current.taskId })
      .then((result: { mode?: AgentMode }) => setWorkMode(result.mode === "execute" ? "execute" : "plan"))
      .catch((e: Error) => setError(e.message));
  }, [current?.taskId]);
  const taskRuns = useMemo(() => {
    const latest = new Map<string, Run>();
    const first = new Map<string, Run>();
    runs.forEach((run) => {
      if (!latest.has(run.taskId)) latest.set(run.taskId, run);
      const initial = first.get(run.taskId);
      if (!initial || new Date(run.created).getTime() < new Date(initial.created).getTime()) first.set(run.taskId, run);
    });
    return Array.from(latest.values()).map((run) => ({ ...run, taskTitle: first.get(run.taskId)?.prompt || run.prompt }));
  }, [runs]);
  const projectRuns = useMemo(() => taskRuns.filter((r) => (r.kind || "project") === "project"), [taskRuns]);
  const chatRuns = useMemo(() => taskRuns.filter((r) => r.kind === "chat"), [taskRuns]);
  const conversationTurns = useMemo(() => {
    if (!current) return [];
    return runs.filter((run) => run.taskId === current.taskId).sort((a, b) => new Date(a.created).getTime() - new Date(b.created).getTime()).map((run) => ({
      run,
      output: events.filter((event) => event.runId === run.id && event.type === "message.delta").map((event) => event.payload.text || "").join(""),
    }));
  }, [current, runs, events]);
  useEffect(() => {
    setActiveTurnId(current?.id || null);
  }, [current?.id, conversationTurns.length]);
  useLayoutEffect(() => {
    if (stickConversationToBottom.current && conversationRef.current) {
      conversationRef.current.scrollTop = conversationRef.current.scrollHeight;
    }
  }, [conversationTurns, events, selected]);
  function syncActiveTurn(container: HTMLElement) {
    const turns = Array.from(container.querySelectorAll<HTMLElement>(".conversation-turn"));
    if (turns.length === 0) return;
    const viewportCenter = container.getBoundingClientRect().top + container.clientHeight / 2;
    const nearest = turns.reduce((closest, turn) => {
      const rect = turn.getBoundingClientRect();
      const distance = Math.abs(rect.top + rect.height / 2 - viewportCenter);
      return distance < closest.distance ? { id: turn.id.slice("turn-".length), distance } : closest;
    }, { id: turns[0].id.slice("turn-".length), distance: Number.POSITIVE_INFINITY });
    setActiveTurnId((currentId) => currentId === nearest.id ? currentId : nearest.id);
  }
  const approvals = useMemo(() => {
    const resolved = new Set(
      events
        .filter((e) => e.type === "approval.resolved")
        .map((e) => e.payload.approvalId),
    );
    return events
      .filter(
        (e) =>
          e.type === "approval.requested" &&
          !resolved.has(e.payload.approvalId),
      )
      .map((e) => e.payload as Approval);
  }, [events]);
  async function chooseWorkspace() {
    const value = await window.agent.chooseWorkspace();
    if (value) {
      try {
        const project = await window.agent.request("project.open", { workspace: value });
        setWorkspace(project.workspace);
        localStorage.setItem("workspace", project.workspace);
        const item = { id: project.workspace, path: project.workspace, name: project.name, isGit: project.isGit };
        setProjects((old) => old.some((p) => p.path.localeCompare(item.path, undefined, { sensitivity: "accent" }) === 0) ? old : [...old, item]);
        setActiveProjectId(item.id);
        setComposeKind("project");
      } catch (e) {
        setError((e as Error).message);
      }
    }
  }
  async function start() {
    setBusy(true);
    stickConversationToBottom.current = true;
    setError("");
    try {
      const run = await window.agent.request(composeKind === "chat" ? "chat.start" : "run.start", {
        workspace,
        providerId: provider,
        model: selectedModel,
        prompt: prompt.trim(),
        mode: workMode,
      });
      setSelected(run.id);
      setPrompt("");
      if (composeKind === "chat") setWorkspace("");
      await refresh();
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }
  function newProjectTask(project: Project) {
    setWorkspace(project.path); localStorage.setItem("workspace", project.path);
    setActiveProjectId(project.id); setComposeKind("project"); setSelected(null); setPrompt(""); setWorkMode("plan");
  }
  function newChatTask() {
    setComposeKind("chat"); setSelected(null); setPrompt(""); setWorkspace(""); setWorkMode("plan");
  }
  function selectComposeKind(kind: "project" | "chat") {
    if (kind === composeKind) return;
    setComposeKind(kind);
    setSelected(null);
    if (kind === "chat") {
      setWorkspace("");
      return;
    }
    const activeProject = projects.find((project) => project.id === activeProjectId);
    if (activeProject) {
      setWorkspace(activeProject.path);
      localStorage.setItem("workspace", activeProject.path);
    }
  }
  function removeProject(project: Project) {
    setProjects((old) => old.filter((p) => p.id !== project.id));
    if (activeProjectId === project.id) { setActiveProjectId(""); setWorkspace(""); localStorage.removeItem("workspace"); }
    setProjectToRemove(null);
  }
  async function continueTask() {
    if (!current || !canContinue || !prompt.trim()) return;
    setBusy(true);
    stickConversationToBottom.current = true;
    setError("");
    try {
      const run = await window.agent.request("run.continue", {
        runId: current.id,
        model: selectedModel,
        prompt: prompt.trim(),
        mode: workMode,
        ...(composeKind === "chat" && workspace ? { workspace } : {}),
      });
      setSelected(run.id);
      setPrompt("");
      await refresh();
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }
  async function setTaskMode(nextMode: AgentMode) {
    if (!current) {
      setWorkMode(nextMode);
      return;
    }
    if (running) return;
    try {
      await window.agent.request("mode.set", { taskId: current.taskId, mode: nextMode });
      setWorkMode(nextMode);
    } catch (e) {
      setError((e as Error).message);
    }
  }
  async function approveAndExecute() {
    if (!current || !canContinue || workMode !== "plan") return;
    setBusy(true);
    stickConversationToBottom.current = true;
    setError("");
    try {
      await window.agent.request("mode.set", { taskId: current.taskId, mode: "execute" });
      setWorkMode("execute");
      const run = await window.agent.request("run.continue", {
        runId: current.id,
        model: selectedModel,
        mode: "execute",
        prompt: prompt.trim() || "请按照已批准的计划开始执行。",
        ...(composeKind === "chat" && workspace ? { workspace } : {}),
      });
      setSelected(run.id);
      setPrompt("");
      await refresh();
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }
  async function respond(approval: Approval, approved: boolean) {
    try {
      await window.agent.request("approval.respond", {
        runId: selected,
        approvalId: approval.approvalId,
        digest: approval.digest,
        approved,
      });
    } catch (e) {
      setError((e as Error).message);
    }
  }
  function openTaskMenu(event: React.MouseEvent, run: Run) {
    event.preventDefault();
    setSelected(run.id);
    setComposeKind(run.kind === "chat" ? "chat" : "project");
    setWorkspace(run.kind === "chat" ? "" : run.workspace);
    setContextMenu({ taskId: run.taskId, x: event.clientX, y: event.clientY });
  }
  async function deleteTask() {
    if (!contextMenu) return;
    const taskId = contextMenu.taskId;
    setContextMenu(null);
    try {
      await window.agent.request("task.delete", { taskId });
      if (runs.find((run) => run.id === selected)?.taskId === taskId) {
        setSelected(null); setPrompt("");
      }
      await refresh();
    } catch (e) { setError((e as Error).message); }
  }
  async function undo(artifact: Artifact) {
    try {
      await window.agent.request("artifact.undo", {
        runId: selected,
        artifactId: artifact.artifactId,
      });
      if (selected) await refreshDiff(selected);
      setError("已撤销文件修改。");
    } catch (e) {
      setError((e as Error).message);
    }
  }
  async function saveSettings() {
    try {
      await window.agent.request("providers.save", {
        providers: allProviders.map((p) => ({
          ...p,
          models: p.models?.length ? p.models : [p.model],
        })),
      });
      setProviders(allProviders);
      setCustomProviders([]);
      setSyncOpen(false);
      setShowApiKey(false);
      setSettings(false);
      setError("模型配置已保存。");
    } catch (e) {
      setError((e as Error).message);
    }
  }
  const updateProvider = (id: string, patch: Partial<Provider>) => {
    setProviders((items) =>
      items.map((p) => (p.id === id ? { ...p, ...patch } : p)),
    );
    setCustomProviders((items) =>
      items.map((p) => (p.id === id ? { ...p, ...patch } : p)),
    );
    if (patch.id && id === settingsProviderId) setSettingsProviderId(patch.id);
  };
  const selectProvider = (id: string) => {
    setProvider(id);
    const item = allProviders.find((p) => p.id === id);
    setSelectedModel(item?.models?.[0] || item?.model || "");
  };
  const selectedSettingsProvider =
    allProviders.find((p) => p.id === settingsProviderId) || allProviders[0];
  const settingsModels = selectedSettingsProvider?.models?.length
    ? selectedSettingsProvider.models
    : selectedSettingsProvider?.model
      ? [selectedSettingsProvider.model]
      : [];
  function removeProvider(id: string) {
    const remaining = allProviders.filter((item) => item.id !== id);
    setProviders((items) => items.filter((item) => item.id !== id));
    setCustomProviders((items) => items.filter((item) => item.id !== id));
    if (settingsProviderId === id) setSettingsProviderId(remaining[0]?.id || "");
    if (provider === id) {
      const next = remaining.find((item) => item.enabled !== false) || remaining[0];
      setProvider(next?.id || "");
      setSelectedModel(next?.models?.[0] || next?.model || "");
    }
    setConnectionResult(null);
  }
  function confirmRemoveProvider() {
    if (!providerToRemove) return;
    removeProvider(providerToRemove.id);
    setProviderToRemove(null);
  }
  async function syncModels() {
    if (!selectedSettingsProvider) return;
    setSyncLoading(true);
    try {
      const result = await window.agent.request("providers.sync", {
        provider: selectedSettingsProvider,
      });
      const models = Array.from(new Set<string>((result.models || []).filter((model: unknown): model is string => typeof model === "string" && model.length > 0)));
      setSyncModelsList(models);
      setSyncSelectedModels([]);
      setSyncOpen(true);
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setSyncLoading(false);
    }
  }
  function addSyncedModels() {
    if (!selectedSettingsProvider || syncSelectedModels.length === 0) return;
    const models = Array.from(new Set([...settingsModels, ...syncSelectedModels]));
    updateProvider(selectedSettingsProvider.id, {
      models,
      model: selectedSettingsProvider.model || models[0] || "",
    });
    setSyncOpen(false);
    setSyncSelectedModels([]);
    setError(`已添加 ${syncSelectedModels.length} 个模型。`);
  }
  async function testConnection() {
    if (!selectedSettingsProvider || testingProviderId) return;
    const providerToTest = selectedSettingsProvider;
    setTestingProviderId(providerToTest.id);
    setConnectionResult(null);
    try {
      const result = await window.agent.request("providers.test", {
        provider: providerToTest,
      });
      setConnectionResult({
        providerId: providerToTest.id,
        ok: Boolean(result.ok),
        message: result.message || (result.ok ? "连接成功。" : "连接失败。"),
      });
    } catch (e) {
      setConnectionResult({
        providerId: providerToTest.id,
        ok: false,
        message: (e as Error).message,
      });
    } finally {
      setTestingProviderId(null);
    }
  }
  function addModel() {
    const value = modelInput.trim();
    if (!selectedSettingsProvider || !value) return;
    const models = Array.from(new Set([...settingsModels, value]));
    updateProvider(selectedSettingsProvider.id, {
      models,
      model: selectedSettingsProvider.model || value,
    });
    setModelInput("");
  }

  return (
    <div className="app-shell" onClick={() => setContextMenu(null)}>
      <aside className="sidebar">
        <div className="brand">
          <span className="brand-mark">N</span>
          <span>
            Niuery <em>Agent</em>
          </span>
        </div>
        <div className="side-heading"><span>项目</span><button aria-label="添加项目" onClick={chooseWorkspace}><Plus size={15} /></button></div>
        <nav className="project-list" aria-label="项目列表">
          {projects.map((project) => {
            const items = projectRuns.filter((r) => r.workspace === project.path);
            return <div className={`project-group ${activeProjectId === project.id ? "active" : ""}`} key={project.id}>
              <div className="project-row">
                <button className="project-name" onClick={() => newProjectTask(project)}><FolderOpen size={14} /><span>{project.name}</span></button>
                <button className="project-add" aria-label={`为${project.name}新建任务`} onClick={() => newProjectTask(project)}><Plus size={14} /></button>
                <button className="project-remove" aria-label={`移除项目${project.name}`} onClick={() => setProjectToRemove(project)}><X size={13} /></button>
              </div>
              {items.map((run) => <button key={run.taskId} className={`task-row nested ${run.id === selected ? "selected" : ""}`} onClick={() => { setSelected(run.id); setComposeKind("project"); setWorkspace(project.path); setActiveProjectId(project.id); }} onContextMenu={(event) => openTaskMenu(event, run)}>
                <span className={`status-mark ${run.status}`}>{statusIcon[run.status]}</span><span className="task-copy"><strong>{run.taskTitle || run.prompt}</strong><small>{statusText[run.status]} · {new Date(run.created).toLocaleDateString("zh-CN")}</small></span>
              </button>)}
            </div>;
          })}
          {projects.length === 0 && <div className="side-empty">还没有项目<br /><span>点击“+”添加项目</span></div>}
          {projectRuns.filter((r) => !projects.some((p) => p.path === r.workspace)).map((run) => <button key={run.taskId} className={`task-row ${run.id === selected ? "selected" : ""}`} onClick={() => { setSelected(run.id); setComposeKind("project"); setWorkspace(run.workspace); }} onContextMenu={(event) => openTaskMenu(event, run)}><span className="status-mark"><AlertTriangle size={13} /></span><span className="task-copy"><strong>{run.taskTitle || run.prompt}</strong><small>项目不可用 · 重新添加项目可恢复</small></span></button>)}
        </nav>
        <div className="side-heading"><span>Chat</span><button aria-label="新建 Chat 任务" onClick={newChatTask}><Plus size={15} /></button></div>
        <nav className="task-list" aria-label="Chat 任务列表">
          {chatRuns.map((run) => <button key={run.taskId} className={`task-row ${run.id === selected ? "selected" : ""}`} onClick={() => { setSelected(run.id); setComposeKind("chat"); setWorkspace(""); }} onContextMenu={(event) => openTaskMenu(event, run)}><span className={`status-mark ${run.status}`}>{statusIcon[run.status]}</span><span className="task-copy"><strong>{run.taskTitle || run.prompt}</strong><small>{statusText[run.status]} · {new Date(run.created).toLocaleDateString("zh-CN")}</small></span></button>)}
          {chatRuns.length === 0 && <div className="side-empty">还没有 Chat 对话</div>}
        </nav>
        <div className="sidebar-footer">
          <button onClick={() => setSettings(true)}>
            <Settings2 size={15} /> 模型服务
          </button>
          <span className="local-state">
            <i /> 本地执行 · 数据仅保存在本机
          </span>
        </div>
      </aside>
      {contextMenu && <div className="task-context-menu" style={{ left: contextMenu.x, top: contextMenu.y }} onClick={(event) => event.stopPropagation()}><button onClick={deleteTask}><X size={14} /> 删除任务</button></div>}
      {projectToRemove && <div className="settings-overlay project-confirm-overlay" role="presentation" onClick={() => setProjectToRemove(null)}><div className="project-confirm" role="dialog" aria-modal="true" aria-labelledby="project-confirm-title" onClick={(event) => event.stopPropagation()}><div className="project-confirm-icon"><AlertTriangle size={20} /></div><div><h2 id="project-confirm-title">移除项目？</h2><p>将从项目列表移除“{projectToRemove.name}”。历史任务不会被删除，重新添加同一路径后可以恢复关联。</p></div><div className="project-confirm-actions"><button className="cancel-button" onClick={() => setProjectToRemove(null)}>取消</button><button className="danger-button" onClick={() => removeProject(projectToRemove)}>确认移除</button></div></div></div>}
      <main className="main-pane">
        <header className="topbar">
          <div className="topbar-title">
            <span className="breadcrumb">
              {composeKind === "chat" ? <MessageCircle size={13} /> : <GitBranch size={13} />} {composeKind === "chat" ? "Chat 对话" : projectName}
            </span>
            <h1>{current ? current.prompt : composeKind === "chat" ? "新的 Chat 对话" : "开始一个新任务"}</h1>
          </div>
          <div className="topbar-actions">
            <button className="icon-button mobile-menu" aria-label="菜单">
              <Menu size={17} />
            </button>
          </div>
        </header>
        {error && (
          <div className="notice" role="alert">
            <AlertTriangle size={15} />
            <span>{error}</span>
            <button onClick={() => setError("")} aria-label="关闭提示">
              <X size={14} />
            </button>
          </div>
        )}
        <section
          ref={conversationRef}
          className="conversation"
          aria-live="polite"
          onScroll={(event) => {
            const element = event.currentTarget;
            stickConversationToBottom.current =
              element.scrollHeight - element.scrollTop - element.clientHeight < 24;
            syncActiveTurn(element);
          }}
        >
          {current && conversationTurns.length > 0 && (
            <nav
              className="keyframe-axis"
              aria-label="对话关键帧"
              onMouseMove={(event) => {
                const frames = Array.from(event.currentTarget.querySelectorAll<HTMLButtonElement>(".keyframe"));
                if (frames.length === 0) return;
                const nearest = frames.reduce((closest, frame, index) => {
                  const distance = Math.abs(frame.getBoundingClientRect().top + frame.offsetHeight / 2 - event.clientY);
                  return distance < closest.distance ? { index, distance } : closest;
                }, { index: 0, distance: Number.POSITIVE_INFINITY });
                setKeyframeHoverIndex((current) => current === nearest.index ? current : nearest.index);
              }}
              onMouseLeave={() => setKeyframeHoverIndex(null)}
            >
              {conversationTurns.map(({ run }, index) => (
                <button
                  key={run.id}
                  className={`keyframe ${run.id === activeTurnId ? "active" : ""}`}
                  aria-label={`跳转到第 ${index + 1} 轮对话`}
                  aria-describedby={`keyframe-tooltip-${run.id}`}
                  title={run.prompt}
                  onMouseEnter={() => setKeyframeHoverIndex(index)}
                  onFocus={() => setKeyframeHoverIndex(index)}
                  onClick={() => {
                    setActiveTurnId(run.id);
                    document.getElementById(`turn-${run.id}`)?.scrollIntoView({ behavior: "smooth", block: "center" });
                  }}
                >
                  <span
                    className="keyframe-bar"
                    style={{ width: `${keyframeHoverIndex === null ? 5 : Math.max(5, 25 - Math.min(Math.abs(index - keyframeHoverIndex), 3) * 8)}px` }}
                  />
                  <span id={`keyframe-tooltip-${run.id}`} className="keyframe-preview" role="tooltip">
                    <strong>第 {index + 1} 轮 · 问题</strong><br />{run.prompt}
                  </span>
                </button>
              ))}
            </nav>
          )}
          <div className="conversation-content">
          {!current ? (
            <div className="welcome">
              <div className="welcome-kicker">
                <SquareTerminal size={14} /> 本地编码工作台
              </div>
              <h2>
                把目标交给 Agent，
                <br />
                <span>一起把它完成。</span>
              </h2>
              <p>
                {composeKind === "chat" ? "这是一个独立的 Chat 对话。" : "选择一个项目，描述你想实现的结果。Agent 会先了解代码，再在需要时请求你的决定。"}
              </p>
              <div className="starter-grid">
                {[
                  ["解释项目结构", "先了解代码的组织方式"],
                  ["修复失败测试", "定位问题并给出最小修改"],
                  ["补充一个测试", "为关键逻辑建立保护"],
                ].map(([title, desc]) => (
                  <button key={title} onClick={() => setPrompt(title)}>
                    <span>{title}</span>
                    <small>{desc}</small>
                    <ArrowUp size={14} />
                  </button>
                ))}
              </div>
            </div>
          ) : (
            <>
              <div className="conversation-meta">
                <span className={`status-chip ${current.status}`}>
                  {statusIcon[current.status]} {statusText[current.status]}
                </span>
                <span className={`mode-badge ${workMode}`}>
                  {workMode === "plan" ? "计划模式" : "执行模式"}
                </span>
                <span>
                  {providers.find((p) => p.id === current.provider)?.model ||
                    current.provider}
                </span>
                <span className="meta-spacer" />
                <button
                  className="review-toggle"
                  onClick={() => setRightOpen((value) => !value)}
                >
                  <PanelRight size={14} /> {rightOpen ? "收起成果" : "查看成果"}
                </button>
              </div>
              {conversationTurns.map(({ run, output: turnOutput }, index) => {
                const timelineEvents = timelineEventsForRun(events, run.id);
                const latestTimelineAction = timelineEvents.length > 0
                  ? timelineEventLabel(timelineEvents[timelineEvents.length - 1])
                  : "暂无执行动作";
                const isTimelineExpanded = timelineExpanded[run.id] ?? false;
                return (
                <div className="conversation-turn" id={`turn-${run.id}`} key={run.id}>
                  <div className="user-prompt">
                    <span className="prompt-label">你的问题</span>
                    <p>{run.prompt}</p>
                  </div>
                  {turnOutput ? (
                    <div className="agent-message">
                      <div className="message-avatar">N</div>
                      <div>
                        <span className="message-label">Agent</span>
                        <div className="answer">
                          <MarkdownAnswer content={turnOutput} />
                        </div>
                      </div>
                    </div>
                  ) : index === conversationTurns.length - 1 && run.status === "running" ? (
                    <ThinkingIndicator />
                  ) : null}
                  <div className="turn-timeline">
                    <button
                      className="timeline-title"
                      type="button"
                      aria-expanded={isTimelineExpanded}
                      aria-controls={`timeline-${run.id}`}
                      title={isTimelineExpanded ? "收起执行记录" : `最新执行动作：${latestTimelineAction}`}
                      onClick={() => setTimelineExpanded((current) => ({
                        ...current,
                        [run.id]: !isTimelineExpanded,
                      }))}
                    >
                      {isTimelineExpanded ? <ChevronDown size={14} /> : <ChevronRight size={14} />}
                      <History size={14} />
                      <span>执行记录</span>
                      {!isTimelineExpanded && <span className="timeline-latest" title={latestTimelineAction}>{latestTimelineAction}</span>}
                    </button>
                    {isTimelineExpanded && (
                      <div id={`timeline-${run.id}`}>
                        {timelineEvents.map((event) => (
                          <div className="timeline-item" key={`${event.runId}-${event.sequence}`}>
                            <span className={`timeline-dot ${event.type.startsWith("run.") ? run.status : ""}`}>{event.type.startsWith("run.") ? statusIcon[run.status] : <Activity size={12} />}</span>
                            <span>{timelineEventLabel(event)}</span>
                            <time>{new Date(event.timestamp).toLocaleTimeString("zh-CN", { hour: "2-digit", minute: "2-digit" })}</time>
                          </div>
                        ))}
                      </div>
                    )}
                  </div>
                </div>
                );
              })}
              {approvals.map((approval) => (
                <div className="approval-card" key={approval.approvalId}>
                  <div className="approval-icon">
                    <ShieldCheck size={18} />
                  </div>
                  <div className="approval-content">
                    <strong>需要你的决定</strong>
                    <p>
                      {approval.name === "修改文件"
                        ? `Agent 想修改 ${approval.arguments?.path || "一个文件"}`
                        : `Agent 想运行 ${approval.arguments?.executable || "一条命令"}`}
                    </p>
                    <small>
                      {approval.name === "运行命令"
                        ? "命令将在选定工作区中使用本机权限执行。"
                        : "修改会在写入前再次校验文件版本。"}
                    </small>
                    <div className="approval-actions">
                      <button
                        className="approve"
                        onClick={() => respond(approval, true)}
                      >
                        <Check size={14} /> 允许一次
                      </button>
                      <button
                        className="deny"
                        onClick={() => respond(approval, false)}
                      >
                        <X size={14} /> 拒绝
                      </button>
                      <button
                        className="details"
                        onClick={() =>
                          setExpanded({
                            ...expanded,
                            [approval.approvalId]:
                              !expanded[approval.approvalId],
                          })
                        }
                      >
                        {expanded[approval.approvalId]
                          ? "收起详情"
                          : "查看详情"}
                      </button>
                    </div>
                    {expanded[approval.approvalId] && (
                      <pre className="approval-details">
                        {JSON.stringify(approval.arguments, null, 2)}
                      </pre>
                    )}
                  </div>
                </div>
              ))}
            </>
          )}
          </div>
        </section>
        <footer className="composer-area">
          <div className="composer">
            <textarea
              aria-label="任务说明"
              value={prompt}
              onChange={(e) => setPrompt(e.target.value)}
              placeholder={current ? (composeKind === "chat" ? "继续聊天…" : "继续描述下一步…") : "描述你想完成的工作…"}
              onKeyDown={(e) => {
                if (
                  e.key === "Enter" &&
                  !e.shiftKey &&
                  !e.nativeEvent.isComposing &&
                  prompt.trim() &&
                  (composeKind === "chat" || workspace) &&
                  (!current || canContinue) &&
                  !busy &&
                  !running
                ) {
                  e.preventDefault();
                  current ? continueTask() : start();
                }
              }}
            />
              <div className="composer-toolbar">
                <label className="mode-select">
                <SlidersHorizontal size={14} />
                <select
                  aria-label="工作模式"
                  title="选择 MAF 工作模式"
                  value={composeKind}
                  onChange={(e) => selectComposeKind(e.target.value as "project" | "chat")}
                >
                  <option value="project">编码模式</option>
                  <option value="chat">Chat 模式</option>
                </select>
                </label>
                <label className="mode-select">
                  <SlidersHorizontal size={14} />
                  <select
                    aria-label="Agent 模式"
                    title="选择 Agent 计划或执行模式"
                    value={workMode}
                    disabled={running || busy}
                    onChange={(e) => void setTaskMode(e.target.value as AgentMode)}
                  >
                    <option value="plan">计划模式</option>
                    <option value="execute">执行模式</option>
                  </select>
                </label>
              <label className="model-select">
                <Settings2 size={14} />
                <select
                  aria-label="模型"
                  value={`${provider}::${selectedModel}`}
                  onChange={(e) => { const [id, ...modelParts] = e.target.value.split('::'); selectProvider(id); setSelectedModel(modelParts.join('::')); }}
                >
                  {allProviders.filter((p) => p.enabled !== false).flatMap((p) => (p.models?.length ? p.models : [p.model]).filter(Boolean).map((model) => (
                    <option key={`${p.id}::${model}`} value={`${p.id}::${model}`}>{p.id} · {model}</option>
                  )))}
                </select>
              </label>
              <span className="composer-hint">
                {composeKind === "chat"
                  ? "Chat 对话 · Enter 发送，Shift + Enter 换行"
                  : !workspace
                  ? "先选择一个项目"
                  : current && !canContinue
                    ? "只有已完成或中断的任务可以继续"
                    : "Enter 发送，Shift + Enter 换行"}
              </span>
              {current && workMode === "plan" && canContinue && !running && (
                <button
                  className="approve-button"
                  type="button"
                  disabled={busy}
                  onClick={() => void approveAndExecute()}
                >
                  <Check size={13} /> 批准并执行
                </button>
              )}
              {running ? (
                <button
                  className="send-button stop"
                  aria-label="停止执行"
                  onClick={() =>
                    window.agent.request("run.cancel", {
                      runId: runs.find((r) => r.status === "running")?.id,
                    })
                  }
                >
                  <CircleStop size={16} />
                </button>
              ) : (
                <button
                  className="send-button"
                  aria-label={current ? (composeKind === "chat" ? "继续对话" : "继续执行") : "开始执行"}
                  disabled={busy || (composeKind !== "chat" && !workspace) || !prompt.trim() || !provider || (!!current && !canContinue)}
                  onClick={current ? continueTask : start}
                >
                  <ArrowUp size={18} />
                </button>
              )}
            </div>
          </div>
          <div className="composer-note">
            <ShieldCheck size={12} /> 修改和命令执行前会请求你的批准
          </div>
        </footer>
      </main>
      {rightOpen && (
        <aside className="result-pane">
          <div className="result-tabs">
            <button
              className={rightTab === "diff" ? "active" : ""}
              onClick={() => setRightTab("diff")}
            >
              <FileDiff size={15} /> 成果{" "}
              {artifacts.length > 0 && <b>{artifacts.length}</b>}
            </button>
            <button
              className={rightTab === "activity" ? "active" : ""}
              onClick={() => setRightTab("activity")}
            >
              <Activity size={15} /> 活动
            </button>
            <button
              className="close-result"
              onClick={() => setRightOpen(false)}
              aria-label="收起成果"
            >
              <ChevronRight size={16} />
            </button>
          </div>
          {rightTab === "diff" ? (
            <div className="result-content">
              {!current || artifacts.length === 0 ? (
                <div className="result-empty">
                  <FileCode2 size={25} />
                  <strong>这里会显示文件成果</strong>
                  <span>Agent 修改文件后，你可以在这里检查、撤销。</span>
                </div>
              ) : (
                artifacts.map((artifact) => (
                  <div className="file-result" key={artifact.artifactId}>
                    <button
                      className="file-result-head"
                      onClick={() =>
                        setExpanded({
                          ...expanded,
                          [artifact.artifactId]: !expanded[artifact.artifactId],
                        })
                      }
                    >
                      <span>
                        <FileCode2 size={14} /> {artifact.path}
                      </span>
                      {expanded[artifact.artifactId] ? (
                        <ChevronDown size={14} />
                      ) : (
                        <ChevronRight size={14} />
                      )}
                    </button>
                    {expanded[artifact.artifactId] && (
                      <>
                        <div className="diff-summary">
                          <span className="added">
                            +{" "}
                            {Math.max(
                              1,
                              artifact.after.split("\n").length -
                                artifact.before.split("\n").length,
                            )}{" "}
                            行
                          </span>
                          <span>
                            {artifact.message || "文件已修改"}
                            {artifact.changedSinceArtifact && "（文件之后又发生了变化）"}
                          </span>
                        </div>
                        <pre className="diff-preview">
                          <code>{artifact.current ?? artifact.after}</code>
                        </pre>
                        <button
                          className="undo-button"
                          onClick={() => undo(artifact)}
                        >
                          <RotateCcw size={13} /> 撤销这次修改
                        </button>
                      </>
                    )}
                  </div>
                ))
              )}
            </div>
          ) : (
            <div className="activity-list">
              {events.map((event) => (
                <div key={`${event.runId}-${event.sequence}`} className="activity-row">
                  <time>
                    {new Date(event.timestamp).toLocaleTimeString("zh-CN")}
                  </time>
                  <span>
                    {event.payload.message || eventText[event.type] || event.type}
                    {eventOutput(event) && (
                      <pre className="activity-output">{eventOutput(event)}</pre>
                    )}
                  </span>
                </div>
              ))}
            </div>
          )}
          <div className="workspace-foot">
            <span>工作区</span>
            <code>{current?.workspace || workspace || "尚未选择项目"}</code>
          </div>
        </aside>
      )}
      {settings && (
        <div className="settings-overlay" onClick={() => { setSyncOpen(false); setSettings(false); }}>
          <section
            className="settings-page"
            onClick={(e) => e.stopPropagation()}
          >
            <header className="settings-header">
              <div>
                <span className="page-kicker">设置</span>
                <h2>模型服务</h2>
                <p>管理 Agent 可以使用的模型和连接方式。</p>
              </div>
              <button
                className="icon-button"
                onClick={() => { setSyncOpen(false); setSettings(false); }}
                aria-label="关闭设置"
              >
                <X size={17} />
              </button>
            </header>
            {error && (
              <div className="notice settings-notice" role="alert">
                <AlertTriangle size={15} />
                <span>{error}</span>
                <button onClick={() => setError("")} aria-label="关闭提示">
                  <X size={14} />
                </button>
              </div>
            )}
            <div className="settings-body">
              <div className="provider-nav">
                <span className="section-caption">已配置服务</span>
                {allProviders.map((p) => (
                  <div key={p.id} className={`provider-nav-item ${p.id === selectedSettingsProvider?.id ? 'active' : ''}`} role="button" tabIndex={0} onClick={() => { setSettingsProviderId(p.id); setShowApiKey(false); }} onKeyDown={(event) => { if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); setSettingsProviderId(p.id); setShowApiKey(false); } }}>
                    <span className="provider-letter">
                      {p.id.slice(0, 1).toUpperCase()}
                    </span>
                    <span>
                      <strong>{p.id}</strong>
                      <small>{p.model || "未配置模型"}</small>
                    </span>
                    <span className={`provider-online ${p.enabled === false ? 'offline' : ''}`} />
                    <button
                      className="provider-nav-actions"
                      aria-label={`删除服务 ${p.id}`}
                      title="删除服务"
                      onClick={(event) => { event.stopPropagation(); setProviderToRemove(p); }}
                    >
                      <Trash2 size={13} />
                    </button>
                  </div>
                ))}
                <button
                  className="add-provider"
                  onClick={() =>
                    setCustomProviders((items) => [
                      ...items,
                      {
                        id: `服务 ${items.length + 1}`,
                        kind: "openai-compatible",
                        model: "",
                        models: [],
                        apiKey: "",
                        enabled: true,
                      },
                    ])
                  }
                >
                  <Plus size={14} /> 添加服务
                </button>
              </div>
              <div className="provider-form">
                <div className="form-heading">
                  <span>服务详情</span>
                  <small>修改后保存才会生效</small>
                </div>
                {selectedSettingsProvider && (
                  <div className="provider-fields" key={selectedSettingsProvider.id}>
                    <div className="provider-status-row"><span>服务状态</span><button className={`status-toggle ${selectedSettingsProvider.enabled !== false ? 'on' : ''}`} onClick={() => updateProvider(selectedSettingsProvider.id, { enabled: selectedSettingsProvider.enabled === false })}>{selectedSettingsProvider.enabled !== false ? '已启用' : '已停用'}</button></div>
                    <label>
                      服务名称
                      <input
                        value={selectedSettingsProvider.id}
                        onChange={(e) =>
                          updateProvider(selectedSettingsProvider.id, { id: e.target.value })
                        }
                      />
                    </label>
                    <label>
                      模型
                      <div className="model-editor">
                        {settingsModels.length > 0 ? (
                          <div className="model-table-wrap">
                            <table className="model-table">
                              <thead><tr><th>模型名称</th><th>状态</th><th aria-label="操作" /></tr></thead>
                              <tbody>
                                {settingsModels.map((model) => (
                                  <tr key={model} className={model === selectedSettingsProvider.model ? "selected" : ""}>
                                    <td><button type="button" className="model-name-button" onClick={() => updateProvider(selectedSettingsProvider.id, { model })}>{model}</button></td>
                                    <td><span className="model-status">{model === selectedSettingsProvider.model ? "默认" : "可用"}</span></td>
                                    <td><button type="button" className="model-remove" aria-label={`删除模型 ${model}`} title="删除模型" onClick={() => updateProvider(selectedSettingsProvider.id, { models: settingsModels.filter(item => item !== model), model: model === selectedSettingsProvider.model ? (settingsModels.find(item => item !== model) || '') : selectedSettingsProvider.model })}><X size={14} /></button></td>
                                  </tr>
                                ))}
                              </tbody>
                            </table>
                          </div>
                        ) : <div className="model-empty">暂未添加模型，请手动添加或同步服务商模型。</div>}
                        <div className="model-add"><input value={modelInput} placeholder="手动添加模型名称" onChange={e => setModelInput(e.target.value)} onKeyDown={e => { if (e.key === 'Enter') { e.preventDefault(); addModel(); } }} /><button type="button" onClick={addModel}><Plus size={13} /> 添加</button></div>
                        <button type="button" className="sync-models" onClick={syncModels} disabled={syncLoading}><RotateCcw size={13} /> {syncLoading ? "同步中…" : "同步模型"}</button>
                      </div>
                    </label>
                    <label>
                      API 地址
                      <input
                        value={selectedSettingsProvider.baseUrl || ""}
                        placeholder="https://api.example.com/v1"
                        onChange={(e) =>
                          updateProvider(selectedSettingsProvider.id, { baseUrl: e.target.value })
                        }
                      />
                    </label>
                    <label>
                      API Key
                      <div className="api-key-input">
                        <input
                          type={showApiKey ? "text" : "password"}
                          value={selectedSettingsProvider.apiKey || ""}
                          placeholder="请输入服务商 API Key"
                          onChange={(e) =>
                            updateProvider(selectedSettingsProvider.id, {
                              apiKey: e.target.value,
                            })
                          }
                        />
                        <button
                          type="button"
                          className="api-key-toggle"
                          aria-label={showApiKey ? "隐藏 API Key" : "显示 API Key"}
                          title={showApiKey ? "隐藏 API Key" : "显示 API Key"}
                          onClick={() => setShowApiKey((visible) => !visible)}
                        >
                          {showApiKey ? <EyeOff size={15} /> : <Eye size={15} />}
                        </button>
                      </div>
                    </label>
                    <p className="form-help">
                      API Key 会写入本机服务配置，不会写入任务或事件。
                    </p>
                    <div className="provider-connection-actions">
                      <button
                        type="button"
                        className="test-connection"
                        onClick={testConnection}
                        disabled={testingProviderId !== null}
                      >
                        <CheckCircle2 size={13} />
                        {testingProviderId === selectedSettingsProvider.id ? "校验中…" : "校验连接"}
                      </button>
                      {connectionResult?.providerId === selectedSettingsProvider.id && (
                        <span className={`connection-result ${connectionResult.ok ? "success" : "failure"}`} role="status">
                          {connectionResult.ok ? <CheckCircle2 size={13} /> : <AlertTriangle size={13} />}
                          {connectionResult.message}
                        </span>
                      )}
                    </div>
                  </div>
                )}
              </div>
            </div>
            <footer className="settings-actions">
              <button
                className="cancel-button"
                onClick={() => { setShowApiKey(false); setSettings(false); }}
              >
                取消
              </button>
              <button className="primary-button" onClick={saveSettings}>
                <Check size={14} /> 保存配置
              </button>
            </footer>
            {syncOpen && (
              <div className="sync-overlay" role="presentation" onClick={() => setSyncOpen(false)}>
                <section className="sync-dialog" role="dialog" aria-modal="true" aria-labelledby="sync-model-title" onClick={(event) => event.stopPropagation()}>
                  <header className="sync-dialog-header">
                    <div><h3 id="sync-model-title">同步模型</h3><p>选择要添加到“{selectedSettingsProvider?.id}”的模型。</p></div>
                    <button className="icon-button" onClick={() => setSyncOpen(false)} aria-label="关闭同步窗口"><X size={16} /></button>
                  </header>
                  <div className="sync-table-wrap">
                    {syncModelsList.length > 0 ? (
                      <table className="sync-table">
                        <thead><tr><th className="sync-check-cell"><span className="sr-only">选择</span></th><th>模型名称</th><th>状态</th></tr></thead>
                        <tbody>{syncModelsList.map((model) => {
                          const alreadyAdded = settingsModels.includes(model);
                          return <tr key={model} className={alreadyAdded ? "already-added" : ""}>
                            <td className="sync-check-cell"><input type="checkbox" checked={alreadyAdded || syncSelectedModels.includes(model)} disabled={alreadyAdded} aria-label={`选择模型 ${model}`} onChange={(event) => setSyncSelectedModels((items) => event.target.checked ? [...items, model] : items.filter((item) => item !== model))} /></td>
                            <td>{model}</td>
                            <td><span className="model-status">{alreadyAdded ? "已添加" : "待添加"}</span></td>
                          </tr>;
                        })}</tbody>
                      </table>
                    ) : <div className="sync-empty">服务商没有返回可用模型。</div>}
                  </div>
                  <footer className="sync-dialog-actions"><button className="cancel-button" onClick={() => setSyncOpen(false)}>取消</button><button className="primary-button" onClick={addSyncedModels} disabled={syncSelectedModels.length === 0}><Check size={14} /> 添加已选模型</button></footer>
                </section>
              </div>
            )}
            {providerToRemove && (
              <div className="sync-overlay provider-delete-overlay" role="presentation" onClick={() => setProviderToRemove(null)}>
                <section className="delete-dialog" role="dialog" aria-modal="true" aria-labelledby="provider-delete-title" onClick={(event) => event.stopPropagation()}>
                  <div className="delete-dialog-icon"><Trash2 size={18} /></div>
                  <div className="delete-dialog-copy">
                    <h3 id="provider-delete-title">删除模型服务？</h3>
                    <p>确定要删除“{providerToRemove.id}”吗？删除后需要点击“保存配置”才会生效。</p>
                  </div>
                  <div className="delete-dialog-actions">
                    <button className="cancel-button" onClick={() => setProviderToRemove(null)}>取消</button>
                    <button className="danger-button" onClick={confirmRemoveProvider}>确认删除</button>
                  </div>
                </section>
              </div>
            )}
          </section>
        </div>
      )}
    </div>
  );
}
createRoot(document.getElementById("root")!).render(<App />);
