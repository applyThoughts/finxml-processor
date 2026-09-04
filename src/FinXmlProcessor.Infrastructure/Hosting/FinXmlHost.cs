using FinXmlProcessor.Application;
using FinXmlProcessor.Application.Abstractions;
using FinXmlProcessor.Application.Processing;
using FinXmlProcessor.Infrastructure.Acquisition;
using FinXmlProcessor.Infrastructure.Agent;
using FinXmlProcessor.Infrastructure.Delivery;
using FinXmlProcessor.Infrastructure.Diagnostics;
using FinXmlProcessor.Infrastructure.Locking;
using FinXmlProcessor.Infrastructure.Logging;
using FinXmlProcessor.Infrastructure.Paths;
using FinXmlProcessor.Infrastructure.Persistence;
using FinXmlProcessor.Infrastructure.Quarantine;
using FinXmlProcessor.Infrastructure.Reports;
using FinXmlProcessor.Infrastructure.Retention;
using FinXmlProcessor.Infrastructure.Scheduling;
using FinXmlProcessor.Infrastructure.Secrets;
using FinXmlProcessor.Infrastructure.Settings;
using FinXmlProcessor.Output.Excel;
using FinXmlProcessor.Processing.Xml;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace FinXmlProcessor.Infrastructure.Hosting;

/// <summary>Shared host composition for the desktop app, the CLI worker and integration tests.</summary>
public static class FinXmlHost
{
    /// <summary>Environment variable prefix for overrides, e.g. FINXML_Processing__MaxInputBytes.</summary>
    public const string EnvironmentPrefix = "FINXML_";

    public static HostApplicationBuilder CreateBuilder(string[] args, bool console, string? rootOverride = null)
    {
        var paths = new AppPaths(rootOverride ?? AppPaths.ResolveDefaultRoot());
        paths.EnsureCreated();

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, DisableDefaults = false });
        builder.Configuration.Sources.Clear();
        builder.Configuration
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true, reloadOnChange: false)
            .AddJsonFile(paths.SettingsFile, optional: true, reloadOnChange: true)
            .AddEnvironmentVariables(EnvironmentPrefix)
            .AddCommandLine(args);

        ConfigureLogging(builder, paths, console);
        builder.Services.AddFinXmlInfrastructure(builder.Configuration, paths);
        return builder;
    }

    public static IServiceCollection AddFinXmlInfrastructure(this IServiceCollection services, IConfiguration configuration, AppPaths paths)
    {
        services.AddSingleton(paths);
        services.AddSingleton<IAppPaths>(paths);
        services.AddOptions<ProcessingOptions>().Bind(configuration.GetSection(ProcessingOptions.SectionName));
        services.AddOptions<ScheduleOptions>().Bind(configuration.GetSection(ScheduleOptions.SectionName));
        services.AddOptions<SftpOptions>().Bind(configuration.GetSection(SftpOptions.SectionName));
        services.AddOptions<DeliveryOptions>().Bind(configuration.GetSection(DeliveryOptions.SectionName));
        services.AddOptions<RetentionOptions>().Bind(configuration.GetSection(RetentionOptions.SectionName));

        services.AddFinXmlApplication();
        services.AddFinXmlXmlProcessing();
        services.AddFinXmlExcelOutput();

        services.AddSingleton(sp => new SqliteProcessingRepository(paths.DatabaseFile, sp.GetRequiredService<ILogger<SqliteProcessingRepository>>()));
        services.AddSingleton<IProcessingRepository>(sp => sp.GetRequiredService<SqliteProcessingRepository>());
        services.AddSingleton<IQuarantineRepository>(sp => sp.GetRequiredService<SqliteProcessingRepository>());
        services.AddSingleton<IFileDuplicateDetector>(sp => sp.GetRequiredService<SqliteProcessingRepository>());
        services.AddSingleton<IRecordDuplicateSetFactory, SqliteRecordDuplicateSetFactory>();
        services.AddSingleton<IProcessingLock, FileProcessingLock>();
        services.AddSingleton<IQuarantineService, QuarantineService>();
        services.AddSingleton<IReportWriter, JsonReportWriter>();
        services.AddSingleton<IOutputDelivery, LocalFolderDelivery>();
        services.AddSingleton<IInputAcquirer, LocalFolderAcquirer>();
        services.AddSingleton<IInputAcquirer, SftpAcquirer>();
        services.AddSingleton<IScheduleService, DailyScheduleService>();
        services.AddSingleton<ScheduledRunCoordinator>();
        services.AddSingleton<UserSettingsStore>();
        services.AddSingleton<RetentionService>();
        services.AddSingleton<DiagnosticsService>();

        if (OperatingSystem.IsMacOS())
        {
            services.TryAddSingleton<ISecretStore, KeychainSecretStore>();
            services.TryAddSingleton<IBackgroundAgentManager, LaunchAgentManager>();
        }
        else if (OperatingSystem.IsWindows())
        {
            services.TryAddSingleton<ISecretStore, DpapiSecretStore>();
            services.TryAddSingleton<IBackgroundAgentManager, NoOpBackgroundAgentManager>();
        }
        else
        {
            services.TryAddSingleton<ISecretStore, UnsupportedSecretStore>();
            services.TryAddSingleton<IBackgroundAgentManager, NoOpBackgroundAgentManager>();
        }

        return services;
    }

    private static void ConfigureLogging(HostApplicationBuilder builder, AppPaths paths, bool console)
    {
        var configuration = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .Enrich.With<RedactingEnricher>()
            .Enrich.WithProperty("Application", AppInfo.ShortName)
            .Enrich.WithProperty("Version", AppInfo.Version)
            .WriteTo.File(new CompactJsonFormatter(), Path.Combine(paths.Logs, "finxml-.json"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 30, fileSizeLimitBytes: 50 * 1024 * 1024, rollOnFileSizeLimit: true, shared: true);
        if (console)
        {
            configuration = configuration.WriteTo.Console(restrictedToMinimumLevel: LogEventLevel.Warning, outputTemplate: "[{Level:u3}] {Message:lj}{NewLine}", formatProvider: System.Globalization.CultureInfo.InvariantCulture);
        }

        Log.Logger = configuration.CreateLogger();
        builder.Logging.ClearProviders();
        builder.Logging.AddSerilog(Log.Logger, dispose: true);
    }
}
