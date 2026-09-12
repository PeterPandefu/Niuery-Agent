using System.Text.Json;

namespace Niuery.Agent.Worker;

public static class Wire
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly object Gate = new();
    public static void Send(object message)
    {
        lock (Gate) Console.WriteLine(JsonSerializer.Serialize(message, Json));
    }
    public static string Required(this JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
        ? value.GetString()! : throw new InvalidOperationException($"缺少有效参数：{name}。");
}
