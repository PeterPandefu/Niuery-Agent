// 根据协议定义生成，请运行 node scripts/generate-protocol.mjs 更新。
using System.Text.Json;
namespace Niuery.Agent.Worker;
public sealed record Request(int Version, string RequestId, string Method, JsonElement Payload);
public static class Protocol { public static readonly string[] Methods = ["hello", "project.open", "providers.list", "providers.save", "providers.test", "providers.sync", "history.list", "events.list", "run.start", "chat.start", "task.delete", "run.cancel", "run.continue", "mode.get", "mode.set", "approval.respond", "workspace.diff", "workspace.read", "workspace.apply", "workspace.command", "artifact.undo", "worktree.create", "worktree.remove", "worker.shutdown"]; }
