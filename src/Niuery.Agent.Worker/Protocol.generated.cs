// 根据协议定义生成，请运行 node scripts/generate-protocol.mjs 更新。
using System.Text.Json;
namespace Niuery.Agent.Worker;
public sealed record Request(int Version, string RequestId, string Method, JsonElement Payload);
public static class Protocol { public static readonly string[] Methods = ["hello", "providers.list", "history.list", "events.list", "run.start", "run.cancel", "run.continue", "approval.respond", "workspace.diff", "artifact.undo", "worker.shutdown"]; }
