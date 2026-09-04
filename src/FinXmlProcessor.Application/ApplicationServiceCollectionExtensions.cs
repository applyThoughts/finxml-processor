using FinXmlProcessor.Application.Abstractions;
using FinXmlProcessor.Application.Processing;
using FinXmlProcessor.Application.Profiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FinXmlProcessor.Application;

public static class ApplicationServiceCollectionExtensions
{
    /// <summary>Registers the processing engine. Infrastructure supplies readers, writers, persistence, secrets and delivery.</summary>
    public static IServiceCollection AddFinXmlApplication(this IServiceCollection services)
    {
        services.TryAddSingleton<IProcessingClock, SystemProcessingClock>();
        services.TryAddSingleton<ProfileLoader>();
        services.TryAddSingleton<IProfileRegistry, FileProfileRegistry>();
        services.TryAddSingleton<ProcessingPipeline>();
        return services;
    }
}
