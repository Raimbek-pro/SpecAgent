using Microsoft.Extensions.DependencyInjection;
using SpecAgent.Ingestion.Parsing;
using SpecAgent.Ingestion.Pii;

namespace SpecAgent.Ingestion;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSpecAgentIngestion(this IServiceCollection services)
    {
        services.AddSingleton<IDocumentParser, DocxDocumentParser>();
        services.AddSingleton<IDocumentParser, PdfDocumentParser>();
        services.AddSingleton<IDocumentParser, TextDocumentParser>();
        services.AddSingleton<DocumentParserRegistry>();
        services.AddSingleton<IPiiVault, InMemoryPiiVault>();
        services.AddSingleton<PiiMasker>();
        return services;
    }
}
