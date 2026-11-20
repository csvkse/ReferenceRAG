using Microsoft.Extensions.DependencyInjection;
using ReferenceRAG.Core.Features.DocumentExtraction.Domain;
using ReferenceRAG.Core.Features.DocumentExtraction.Extractors;
using ReferenceRAG.Core.Features.DocumentExtraction.Services;
using ReferenceRAG.Core.Features.DocumentExtraction.Strategies;

namespace ReferenceRAG.Core.Features.DocumentExtraction;

public static class DocumentExtractionExtensions
{
    public static IServiceCollection AddDocumentExtractionServices(this IServiceCollection services)
    {
        services.AddSingleton<IScannedPdfHandlingStrategy, GracefulDegradeScannedStrategy>();
        services.AddSingleton<IDocumentContentExtractor, PdfPigDocumentExtractor>();
        services.AddSingleton<IDocumentContentExtractor, PlainTextDocumentExtractor>();
        services.AddSingleton<IDocumentContentExtractor, UnsupportedBinaryDocumentExtractor>();
        services.AddSingleton<IDocumentExtractorRouter, DocumentExtractorRouter>();

        return services;
    }
}
