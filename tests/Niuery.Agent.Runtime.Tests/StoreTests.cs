using Niuery.Agent.Worker;
using Xunit;
namespace Niuery.Agent.Runtime.Tests;

public sealed class StoreTests
{
    [Fact(DisplayName = "任务状态保存并恢复模式和会话快照")]
    public void TaskStateRoundTrip()
    {
        var directory = Directory.CreateTempSubdirectory("niuery-task-state-").FullName;
        using var store = new Store(Path.Combine(directory, "test.db"));

        store.SaveTaskState("task-1", "plan", "{\"history\":[]}");
        var initial = store.LoadTaskState("task-1");
        Assert.NotNull(initial);
        Assert.Equal("plan", initial.Mode);
        Assert.Equal("{\"history\":[]}", initial.SessionJson);

        store.SaveTaskState("task-1", "execute", "{\"history\":[1]}");
        var updated = store.LoadTaskState("task-1");
        Assert.NotNull(updated);
        Assert.Equal("execute", updated.Mode);
        Assert.Equal("{\"history\":[1]}", updated.SessionJson);
    }

    [Fact(DisplayName = "任务状态拒绝未知模式")]
    public void TaskStateRejectsUnknownMode()
    {
        var directory = Directory.CreateTempSubdirectory("niuery-task-state-invalid-").FullName;
        using var store = new Store(Path.Combine(directory, "test.db"));

        Assert.Throws<InvalidOperationException>(() => store.SaveTaskState("task-1", "goal", null));
    }

    [Fact(DisplayName = "并发事件序号唯一且重开保留终态并标记中断")]
    public void PersistAndRecover()
    {
        var directory = Directory.CreateTempSubdirectory("niuery-store-").FullName;
        var database = Path.Combine(directory, "test.db");
        string complete, interrupted;
        using (var store = new Store(database))
        {
            complete = store.Create(directory, "已完成任务", "test").Id;
            Parallel.For(0, 30, index => store.Append(complete, "message.delta", new { text = index.ToString() }));
            store.Append(complete, "run.completed", new { message = "完成" }, "completed");
            interrupted = store.Create(directory, "中断任务", "test").Id;
        }
        using (var recovered = new Store(database))
        {
            Assert.Equal("completed", recovered.History().Single(x => x.Id == complete).Status);
            Assert.Equal("interrupted", recovered.History().Single(x => x.Id == interrupted).Status);
            Assert.Equal(Enumerable.Range(1, 31).Select(x => (long)x), recovered.Events(complete).Select(x => x.Sequence));
            Assert.Equal(11, recovered.Events(complete, 20).Count);
        }
    }
}
