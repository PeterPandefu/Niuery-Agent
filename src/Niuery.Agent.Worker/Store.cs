using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace Niuery.Agent.Worker;

public sealed class Store : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly object gate = new();
    public Store(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        connection.Open();
        Execute("PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS runs(id TEXT PRIMARY KEY, taskId TEXT NOT NULL, parentId TEXT, workspace TEXT NOT NULL DEFAULT '', prompt TEXT NOT NULL, provider TEXT NOT NULL, status TEXT NOT NULL, created TEXT NOT NULL, kind TEXT NOT NULL DEFAULT 'project'); CREATE TABLE IF NOT EXISTS events(runId TEXT NOT NULL, sequence INTEGER NOT NULL, type TEXT NOT NULL, timestamp TEXT NOT NULL, payload TEXT NOT NULL, PRIMARY KEY(runId,sequence)); CREATE TABLE IF NOT EXISTS task_state(taskId TEXT PRIMARY KEY, mode TEXT NOT NULL DEFAULT 'execute', sessionJson TEXT, updatedAt TEXT NOT NULL);");
        try { Execute("ALTER TABLE runs ADD COLUMN kind TEXT NOT NULL DEFAULT 'project'"); } catch (SqliteException) { }
        foreach (var run in History().Where(r => r.Status == "running"))
            Append(run.Id, "run.interrupted", new { message = "执行器退出，执行已中断。继续前请检查工作区差异。" }, "interrupted");
    }
    private void Execute(string sql, params (string, object?)[] values)
    {
        using var command = connection.CreateCommand(); command.CommandText = sql;
        foreach (var (key, value) in values) command.Parameters.AddWithValue(key, value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }
    public RunRow Create(string kind, string workspace, string prompt, string provider, string? parentId = null)
    {
        lock (gate)
        {
            var parent = parentId is null ? null : History().SingleOrDefault(r => r.Id == parentId)
                ?? throw new InvalidOperationException("找不到前序执行。");
            var row = new RunRow(Guid.NewGuid().ToString("N"), parent?.TaskId ?? Guid.NewGuid().ToString("N"), parentId,
                kind, workspace, prompt, provider, "running", DateTimeOffset.UtcNow.ToString("O"));
            Execute("INSERT INTO runs(id,taskId,parentId,workspace,prompt,provider,status,created,kind) VALUES($id,$task,$parent,$workspace,$prompt,$provider,$status,$created,$kind)",
                ("$id", row.Id), ("$task", row.TaskId), ("$parent", parentId), ("$workspace", workspace), ("$prompt", prompt),
                ("$provider", provider), ("$status", row.Status), ("$created", row.Created), ("$kind", kind));
            return row;
        }
    }
    public RunRow Create(string workspace, string prompt, string provider, string? parentId = null) => Create("project", workspace, prompt, provider, parentId);
    public void DeleteTask(string taskId)
    {
        lock (gate)
        {
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "DELETE FROM events WHERE runId IN (SELECT id FROM runs WHERE taskId=$taskId); DELETE FROM runs WHERE taskId=$taskId; DELETE FROM task_state WHERE taskId=$taskId;";
            command.Parameters.AddWithValue("$taskId", taskId);
            command.ExecuteNonQuery();
            transaction.Commit();
        }
    }
    public TaskState? LoadTaskState(string taskId)
    {
        lock (gate)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT mode,sessionJson,updatedAt FROM task_state WHERE taskId=$taskId";
            command.Parameters.AddWithValue("$taskId", taskId);
            using var reader = command.ExecuteReader();
            return reader.Read()
                ? new TaskState(taskId, reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2))
                : null;
        }
    }
    public void SaveTaskState(string taskId, string mode, string? sessionJson)
    {
        if (mode is not ("plan" or "execute"))
            throw new InvalidOperationException("任务模式必须是 plan 或 execute。");
        lock (gate)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO task_state(taskId,mode,sessionJson,updatedAt) VALUES($taskId,$mode,$sessionJson,$updatedAt) ON CONFLICT(taskId) DO UPDATE SET mode=$mode,sessionJson=$sessionJson,updatedAt=$updatedAt";
            command.Parameters.AddWithValue("$taskId", taskId);
            command.Parameters.AddWithValue("$mode", mode);
            command.Parameters.AddWithValue("$sessionJson", (object?)sessionJson ?? DBNull.Value);
            command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }
    }
    public List<RunRow> History(int limit = 200, int offset = 0)
    {
        lock (gate)
        {
            limit = Math.Clamp(limit, 1, 1000); offset = Math.Max(0, offset);
            using var cmd = connection.CreateCommand(); cmd.CommandText = "SELECT id,taskId,parentId,workspace,prompt,provider,status,created,kind FROM runs ORDER BY created DESC LIMIT $limit OFFSET $offset";
            cmd.Parameters.AddWithValue("$limit", limit); cmd.Parameters.AddWithValue("$offset", offset);
            using var reader = cmd.ExecuteReader(); var rows = new List<RunRow>();
            while (reader.Read()) rows.Add(new(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(8), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7)));
            return rows;
        }
    }
    public StoredEvent Append(string runId, string type, object payload, string? status = null)
    {
        lock (gate)
        {
            using var transaction = connection.BeginTransaction();
            using var cmd = connection.CreateCommand(); cmd.Transaction = transaction;
            cmd.CommandText = "SELECT COALESCE(MAX(sequence),0)+1 FROM events WHERE runId=$id"; cmd.Parameters.AddWithValue("$id", runId);
            var sequence = Convert.ToInt64(cmd.ExecuteScalar());
            var timestamp = DateTimeOffset.UtcNow.ToString("O");
            var element = JsonSerializer.SerializeToElement(payload, Wire.Json);
            cmd.CommandText = "INSERT INTO events VALUES($id,$sequence,$type,$timestamp,$payload)";
            cmd.Parameters.AddWithValue("$sequence", sequence); cmd.Parameters.AddWithValue("$type", type);
            cmd.Parameters.AddWithValue("$timestamp", timestamp); cmd.Parameters.AddWithValue("$payload", element.GetRawText()); cmd.ExecuteNonQuery();
            if (status is not null)
            {
                cmd.CommandText = "UPDATE runs SET status=$status WHERE id=$id"; cmd.Parameters.AddWithValue("$status", status); cmd.ExecuteNonQuery();
            }
            transaction.Commit();
            return new(runId, sequence, type, timestamp, element);
        }
    }
    public List<StoredEvent> Events(string runId, long after = 0, int limit = 1000)
    {
        lock (gate)
        {
            limit = Math.Clamp(limit, 1, 5000);
            using var cmd = connection.CreateCommand(); cmd.CommandText = "SELECT sequence,type,timestamp,payload FROM events WHERE runId=$id AND sequence>$after ORDER BY sequence LIMIT $limit";
            cmd.Parameters.AddWithValue("$id", runId); cmd.Parameters.AddWithValue("$after", after); cmd.Parameters.AddWithValue("$limit", limit);
            using var reader = cmd.ExecuteReader(); var events = new List<StoredEvent>();
            while (reader.Read()) events.Add(new(runId, reader.GetInt64(0), reader.GetString(1), reader.GetString(2), JsonSerializer.Deserialize<JsonElement>(reader.GetString(3))));
            return events;
        }
    }
    public void Dispose() => connection.Dispose();
}

public sealed record RunRow(string Id, string TaskId, string? ParentId, string Kind, string Workspace, string Prompt, string Provider, string Status, string Created);
public sealed record StoredEvent(string RunId, long Sequence, string Type, string Timestamp, JsonElement Payload);
public sealed record TaskState(string TaskId, string Mode, string? SessionJson, string UpdatedAt);
