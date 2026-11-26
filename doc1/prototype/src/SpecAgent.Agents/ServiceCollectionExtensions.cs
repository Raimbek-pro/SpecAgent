using Anthropic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;
using SpecAgent.Agents.Agents;
using SpecAgent.Agents.Knowledge;
using SpecAgent.Agents.Llm;
using SpecAgent.Agents.Orchestration;
using SpecAgent.Agents.Storage;
using SpecAgent.Agents.Telemetry;
using SpecAgent.Agents.Tools;
using SpecAgent.Core.Events;
using SpecAgent.Ingestion.Pii;

namespace SpecAgent.Agents;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSpecAgents(this IServiceCollection services, IConfiguration config, string contentRoot)
    {
        services.Configure<ModelRoutingOptions>(config.GetSection(ModelRoutingOptions.Section));
        services.Configure<PatchOptions>(config.GetSection("Patches"));

        // Event store, encrypted PII vault, settings & admin audit trail:
        // InMemory for dev/tests, Postgres for anything that must survive a restart.
        if (config["EventStore"] is "Postgres" && config.GetConnectionString("Postgres") is { } cs)
        {
            services.AddSingleton(NpgsqlDataSource.Create(cs));
            services.AddSingleton<PostgresEventStore>();
            services.AddSingleton<IEventStore>(sp => sp.GetRequiredService<PostgresEventStore>());
            services.AddSingleton<PostgresAdminAuditLog>();
            services.AddSingleton<IAdminAuditLog>(sp => sp.GetRequiredService<PostgresAdminAuditLog>());
            services.AddSingleton<PostgresSystemSettingsStore>();
            services.AddSingleton<ISystemSettingsStore>(sp => sp.GetRequiredService<PostgresSystemSettingsStore>());
            services.AddSingleton<PostgresSessionUsageStore>();
            services.AddSingleton<ISessionUsageStore>(sp => sp.GetRequiredService<PostgresSessionUsageStore>());

            var vaultKey = config["PiiVault:Key"] ?? config["PII_VAULT_KEY"] ?? Environment.GetEnvironmentVariable("PII_VAULT_KEY");
            var ttlDays = int.TryParse(config["PiiVault:TtlDays"], out var days) ? days : 30;
            services.AddSingleton(sp => new PostgresPiiVault(
                sp.GetRequiredService<NpgsqlDataSource>(),
                vaultKey,
                ttlDays,
                sp.GetRequiredService<ILogger<PostgresPiiVault>>()));
            services.Replace(ServiceDescriptor.Singleton<IPiiVault>(sp => sp.GetRequiredService<PostgresPiiVault>()));
            services.AddHostedService<PiiVaultCleanupWorker>();
        }
        else
        {
            services.AddSingleton<IEventStore, InMemoryEventStore>();
            services.AddSingleton<IAdminAuditLog, InMemoryAdminAuditLog>();
            services.AddSingleton<ISystemSettingsStore, InMemorySystemSettingsStore>();
            services.AddSingleton<ISessionUsageStore, InMemorySessionUsageStore>();
        }

        services.AddSingleton<SpecAgentMetrics>();

        var kbPath = config["Knowledge:Path"] ?? "knowledge";
        if (!Path.IsPathRooted(kbPath)) kbPath = Path.GetFullPath(Path.Combine(contentRoot, kbPath));
        services.AddSingleton<IEmbeddingClient, FreeEmbeddingClient>();
        services.AddSingleton<IKnowledgeBase>(sp => new SemanticKnowledgeBase(
            kbPath,
            sp.GetRequiredService<IEmbeddingClient>(),
            config,
            sp.GetRequiredService<ILogger<SemanticKnowledgeBase>>()));

        if (string.Equals(config["Llm:Provider"], "Anthropic", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton(_ => new AnthropicClient());
            services.AddSingleton<ILlmClient, ClaudeLlmClient>();
        }
        else
        {
            services.AddSingleton<ILlmClient, OpenAiCompatibleLlmClient>();
        }
        services.AddSingleton<PromptLibrary>();
        services.AddSingleton<ToolGateway>();
        services.AddSingleton<SupervisorAgent>();
        services.AddSingleton(sp => new ChecklistAgent(
            sp.GetRequiredService<ILlmClient>(),
            sp.GetRequiredService<PromptLibrary>(),
            kbPath));
        services.AddSingleton<MetaCriticAgent>();
        services.AddSingleton<PatchLoop>();
        services.AddScoped<AuditOrchestrator>();
        services.AddSingleton<AuditQueue>();
        services.AddSingleton<AuditSessionRegistry>();
        services.AddHostedService<AuditWorker>();
        return services;
    }
}
