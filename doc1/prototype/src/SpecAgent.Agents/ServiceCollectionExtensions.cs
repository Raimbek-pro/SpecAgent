using Anthropic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SpecAgent.Agents.Agents;
using SpecAgent.Agents.Knowledge;
using SpecAgent.Agents.Llm;
using SpecAgent.Agents.Orchestration;
using SpecAgent.Agents.Storage;
using SpecAgent.Agents.Tools;
using SpecAgent.Core.Events;

namespace SpecAgent.Agents;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSpecAgents(this IServiceCollection services, IConfiguration config, string contentRoot)
    {
        services.Configure<ModelRoutingOptions>(config.GetSection(ModelRoutingOptions.Section));
        services.Configure<PatchOptions>(config.GetSection("Patches"));

        // Event store: the session log. InMemory for dev/tests, Postgres for anything that must survive a restart.
        if (config["EventStore"] is "Postgres" && config.GetConnectionString("Postgres") is { } cs)
        {
            services.AddSingleton(NpgsqlDataSource.Create(cs));
            services.AddSingleton<PostgresEventStore>();
            services.AddSingleton<IEventStore>(sp => sp.GetRequiredService<PostgresEventStore>());
        }
        else
        {
            services.AddSingleton<IEventStore, InMemoryEventStore>();
        }

        var kbPath = config["Knowledge:Path"] ?? "knowledge";
        if (!Path.IsPathRooted(kbPath)) kbPath = Path.GetFullPath(Path.Combine(contentRoot, kbPath));
        services.AddSingleton<IKnowledgeBase>(new MarkdownKnowledgeBase(kbPath));

        // ANTHROPIC_API_KEY (or another SDK credential source) is read by the SDK itself.
        services.AddSingleton(_ => new AnthropicClient());
        services.AddSingleton<ILlmClient, ClaudeLlmClient>();
        services.AddSingleton<PromptLibrary>();
        services.AddSingleton<ToolGateway>();
        services.AddSingleton<SupervisorAgent>();
        services.AddSingleton<MetaCriticAgent>();
        services.AddSingleton<PatchLoop>();
        services.AddScoped<AuditOrchestrator>();
        services.AddSingleton<AuditQueue>();
        services.AddHostedService<AuditWorker>();
        return services;
    }
}
