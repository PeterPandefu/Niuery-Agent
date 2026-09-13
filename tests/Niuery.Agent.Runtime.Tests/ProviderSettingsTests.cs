using System.Text.Json;
using Niuery.Agent.Runtime.Maf;
using Niuery.Agent.Worker;
using Xunit;

namespace Niuery.Agent.Runtime.Tests;

public sealed class ProviderSettingsTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private static ProviderConfiguration Ollama => new(
        "local", "ollama", "http://127.0.0.1:11434/v1/", "installed-model", null);
    private static ProviderConfiguration Disabled => new(
        "compatible", "openai-compatible", "", "", null, Enabled: false);

    [Fact(DisplayName = "仅启用 Ollama 时允许保存未完成的停用配置并重新加载")]
    public async Task SaveDisabledDraftWithOllama()
    {
        var directory = Directory.CreateTempSubdirectory("niuery-provider-settings-");
        var configPath = Path.Combine(directory.FullName, "providers.json");
        using var store = new Store(Path.Combine(directory.FullName, "tasks.db"));
        await using var host = new WorkerHost(store, [Ollama], configPath);
        var payload = JsonSerializer.SerializeToElement(new { providers = new[] { Disabled, Ollama } }, WebJson);

        await host.Handle("providers.save", payload);

        var saved = await ProviderConfiguration.LoadAsync(configPath);
        Assert.Equal(2, saved.Count);
        Assert.False(saved[0].Enabled);
        Assert.Equal("", saved[0].Model);
        Assert.Equal(Ollama.Id, saved[1].Id);
        Assert.Equal(Ollama.Model, saved[1].Model);
        Assert.True(saved[1].Enabled);
        var listed = JsonSerializer.SerializeToElement(await host.Handle("providers.list", default), WebJson);
        Assert.False(listed[0].GetProperty("enabled").GetBoolean());
        Assert.Equal(Ollama.Model, listed[1].GetProperty("model").GetString());
    }

    [Fact(DisplayName = "前端的小驼峰字段可以保存有效的 Ollama 配置")]
    public async Task SaveCamelCaseOllama()
    {
        var directory = Directory.CreateTempSubdirectory("niuery-provider-settings-");
        var configPath = Path.Combine(directory.FullName, "providers.json");
        using var store = new Store(Path.Combine(directory.FullName, "tasks.db"));
        await using var host = new WorkerHost(store, [Ollama], configPath);
        var payload = JsonSerializer.SerializeToElement(new { providers = new[] { Ollama } }, WebJson);

        await host.Handle("providers.save", payload);

        var saved = Assert.Single(await ProviderConfiguration.LoadAsync(configPath));
        Assert.Equal(Ollama.Model, saved.Model);
    }
}
