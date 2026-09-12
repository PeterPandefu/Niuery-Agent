using System.Text;
using System.Text.Json;
using Niuery.Agent.Runtime.Maf;
using Niuery.Agent.Worker;

Console.InputEncoding = Encoding.UTF8; Console.OutputEncoding = new UTF8Encoding(false);
if (args.Length != 2) { Console.Error.WriteLine("用法：执行器 <模型配置文件> <数据库路径>"); return 2; }
try
{
    var providers = await ProviderConfiguration.LoadAsync(args[0]);
    using var store = new Store(args[1]);
    await using var host = new WorkerHost(store, providers);
    while (await Console.In.ReadLineAsync() is { } line)
    {
        string? requestId = null;
        try
        {
            if (line.Length > 1024 * 1024) throw new InvalidOperationException("协议消息超过长度限制。");
            using var document = JsonDocument.Parse(line); var request = document.RootElement;
            requestId = request.Required("requestId");
            if (request.GetProperty("version").GetInt32() != 1) throw new InvalidOperationException("协议版本不兼容。");
            var method = request.Required("method");
            if (!Protocol.Methods.Contains(method)) throw new InvalidOperationException("不支持的协议方法。");
            if (request.GetProperty("payload").ValueKind != JsonValueKind.Object) throw new InvalidOperationException("协议参数必须是对象。");
            var result = await host.Handle(method, request.GetProperty("payload"));
            Wire.Send(new { version = 1, requestId, ok = true, payload = result });
            if (method == "worker.shutdown") break;
        }
        catch (Exception ex)
        {
            var message = ex is InvalidOperationException or ConfigurationException ? ex.Message : "请求处理失败，请检查参数或执行器状态。";
            Wire.Send(new { version = 1, requestId, ok = false, error = new { code = "REQUEST_FAILED", message } });
        }
    }
    return 0;
}
catch (Exception) { Console.Error.WriteLine("执行器启动失败，请检查模型配置和数据库权限。"); return 1; }
