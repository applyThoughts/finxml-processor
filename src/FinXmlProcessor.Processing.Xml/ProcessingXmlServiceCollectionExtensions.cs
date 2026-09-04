using FinXmlProcessor.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FinXmlProcessor.Processing.Xml;

public static class ProcessingXmlServiceCollectionExtensions
{
    public static IServiceCollection AddFinXmlXmlProcessing(this IServiceCollection services)
    {
        services.TryAddSingleton<IRecordReaderFactory, StreamingXmlRecordReaderFactory>();
        services.TryAddSingleton<IInputValidator, XmlInputValidator>();
        services.AddSingleton<IRecordMapperFactory, ProfileRecordMapperFactory>();
        return services;
    }
}
