using Niuery.Agent.Worker;
using Xunit;
namespace Niuery.Agent.Runtime.Tests;

public sealed class StoreTests
{
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
