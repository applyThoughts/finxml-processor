using FinXmlProcessor.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FinXmlProcessor.Output.Excel;

public static class OutputExcelServiceCollectionExtensions
{
    public static IServiceCollection AddFinXmlExcelOutput(this IServiceCollection services)
    {
        services.TryAddSingleton<IWorkbookWriter, StreamingXlsxWorkbookWriter>();
        return services;
    }
}
