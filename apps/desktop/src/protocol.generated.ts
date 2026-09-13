// 根据协议定义生成，请运行 node scripts/generate-protocol.mjs 更新。
export type Method = "hello" | "project.open" | "providers.list" | "providers.save" | "providers.test" | "providers.sync" | "history.list" | "events.list" | "run.start" | "chat.start" | "task.delete" | "run.cancel" | "run.continue" | "approval.respond" | "workspace.diff" | "workspace.read" | "workspace.apply" | "workspace.command" | "artifact.undo" | "worktree.create" | "worktree.remove" | "worker.shutdown";
export interface Request { version: 1; requestId: string; method: Method; payload: Record<string, unknown> }
