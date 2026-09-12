using System.Text.Json;

namespace Niuery.Agent.Runtime.Maf;

public sealed record ProviderConfiguration(
    string Id,
    string Kind,
    string BaseUrl,
    string Model,
    string? ApiKeyEnvironmentVariable,
    bool SupportsTools = true,
    bool SupportsStreaming = true,
    int TimeoutSeconds = 60)
{
    public Uri Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || string.IsNullOrWhiteSpace(Model) || Model.Contains("请填写"))
            throw new ConfigurationException("请填写提供商编号和实际模型名。");
        if (Kind is not ("openai-compatible" or "ollama"))
            throw new ConfigurationException("提供商类型必须为 openai-compatible 或 ollama。");
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            !(uri.Scheme == "https" || (uri.Scheme == "http" && uri.IsLoopback)))
            throw new ConfigurationException("模型地址必须使用 HTTPS；仅回环地址允许 HTTP，地址不得包含凭证、查询参数或片段。");
        if (uri.Host.Equals("example.com", StringComparison.OrdinalIgnoreCase))
            throw new ConfigurationException("请将示例地址替换为真实模型服务地址。");
        if (!SupportsTools || !SupportsStreaming)
            throw new ConfigurationException("此验收要求模型同时支持工具调用和流式输出。");
        if (TimeoutSeconds is < 1 or > 600)
            throw new ConfigurationException("请求时限必须在 1 到 600 秒之间。");
        if (Kind == "openai-compatible" && string.IsNullOrWhiteSpace(ApiKeyEnvironmentVariable))
            throw new ConfigurationException("OpenAI 兼容服务需要配置密钥环境变量名。");
        return uri;
    }

    public string ResolveApiKey()
    {
        Validate();
        if (Kind == "ollama" && string.IsNullOrWhiteSpace(ApiKeyEnvironmentVariable))
            return "ollama";
        var value = Environment.GetEnvironmentVariable(ApiKeyEnvironmentVariable!);
        if (string.IsNullOrWhiteSpace(value) && OperatingSystem.IsWindows())
            value = Environment.GetEnvironmentVariable(ApiKeyEnvironmentVariable!, EnvironmentVariableTarget.User);
        if (string.IsNullOrWhiteSpace(value) && OperatingSystem.IsWindows())
            value = Environment.GetEnvironmentVariable(ApiKeyEnvironmentVariable!, EnvironmentVariableTarget.Machine);
        if (string.IsNullOrWhiteSpace(value))
            throw new ConfigurationException("未找到配置所引用的密钥环境变量，请在本机设置后重新启动进程。");
        return value;
    }

    public static async Task<IReadOnlyList<ProviderConfiguration>> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        var json = await File.ReadAllTextAsync(path, cancellationToken);
        var document = JsonSerializer.Deserialize<ProviderFile>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
        }) ?? throw new ConfigurationException("模型配置文件为空。");
        if (document.Providers is not { Count: > 0 })
            throw new ConfigurationException("模型配置至少需要一个提供商。");
        if (document.Providers.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != document.Providers.Count)
            throw new ConfigurationException("提供商编号不能重复。");
        return document.Providers;
    }

    private sealed record ProviderFile(List<ProviderConfiguration>? Providers);
}

public sealed class ConfigurationException(string message) : Exception(message);
