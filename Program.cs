using FileWatcherApp.Messaging;
using FileWatcherApp.Services.DXFAnalysis;
using FileWatcherApp.Services.FileWatcher;
using FileWatcherApp.Services.DXFAnalysis.Storage;
using FileWatcherApp.Services.DXFAnalysis.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace FileWatcherApp;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

#if !DEBUG
        builder.Environment.EnvironmentName = "Production";
#endif

        var environment = builder.Environment;
        var configFiles = GatherConfigFiles(environment);
        Console.WriteLine($"[BOOT] Environment={environment.EnvironmentName} ConfigFiles={string.Join(", ", configFiles.Select(Path.GetFileName))}");
        var osDescription = RuntimeInformation.OSDescription.Trim();
        Console.WriteLine($"[BOOT] OS={osDescription} Arch={RuntimeInformation.OSArchitecture} Machine={Environment.MachineName}");

        builder.Logging.AddConsole();

        var scoringSection = builder.Configuration.GetSection("DXFAnalysis:Scoring");
        var numCurvesWeight = builder.Configuration.GetValue<double?>("DXFAnalysis:Scoring:NumCurvesWeight");
        var debugOptions = new DXFAnalysisOptions();
        builder.Configuration.GetSection("DXFAnalysis").Bind(debugOptions);
        var extraThresholds = debugOptions.Scoring?.NumCurvesExtraThresholds ?? new();
        var stepWeight = debugOptions.Scoring?.NumCurvesStepWeight ?? double.NaN;
        var firstThreshold = extraThresholds.FirstOrDefault();
        var firstThresholdInfo = firstThreshold is null ? "none" : $"{firstThreshold.Threshold}/{firstThreshold.Weight}";
        Console.WriteLine($"[BOOT] Scoring={scoringSection.GetChildren().Count()} NumCurvesWeight={numCurvesWeight?.ToString("0.###") ?? "null"} ExtraThresholds={extraThresholds.Count} FirstExtra={firstThresholdInfo} StepWeight={stepWeight:0.###}");

        builder.Services.Configure<RabbitMqOptions>(builder.Configuration.GetSection("RabbitMq"));
        builder.Services.Configure<FileWatcherOptions>(builder.Configuration.GetSection("FileWatcher"));
        builder.Services.Configure<DXFAnalysisOptions>(builder.Configuration.GetSection("DXFAnalysis"));

        builder.Services.AddSingleton<IImageStorageClient>(sp =>
        {
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var analysisOptions = sp.GetRequiredService<IOptions<DXFAnalysisOptions>>().Value;
            var imageOptions = analysisOptions.ImageStorage ?? new DXFImageStorageOptions();

            if (imageOptions.Enabled && string.Equals(imageOptions.Provider, "s3", StringComparison.OrdinalIgnoreCase))
            {
                return new S3ImageStorageClient(imageOptions, loggerFactory.CreateLogger<S3ImageStorageClient>());
            }

            return new NullImageStorageClient(loggerFactory.CreateLogger<NullImageStorageClient>());
        });

        builder.Services.AddSingleton<DXFPreprocessor>();
        builder.Services.AddSingleton<DXFAnalyzer>();
        builder.Services.AddSingleton<CalibratedDxfRenderer>();
        builder.Services.AddSingleton<DXFImageRenderer>();
        builder.Services.AddSingleton<ComplexityScorer>();
        builder.Services.AddSingleton<DXFAnalysisCache>();

        builder.Services.AddSingleton<RabbitMqConnection>();
        builder.Services.AddHostedService<RpcPingResponderService>();
        builder.Services.AddHostedService<FileWatcherService>();
        builder.Services.AddHostedService<DXFAnalysisWorker>();
        builder.Services.AddHostedService<FileCommandConsumer>();

        var host = builder.Build();
        await host.RunAsync();
    }

    private static IReadOnlyCollection<string> GatherConfigFiles(IHostEnvironment environment)
    {
        var files = new List<string>();
        var basePath = Path.Combine(environment.ContentRootPath, "appsettings.json");
        if (File.Exists(basePath))
        {
            files.Add(basePath);
        }

        if (!string.IsNullOrWhiteSpace(environment.EnvironmentName))
        {
            var envPath = Path.Combine(environment.ContentRootPath, $"appsettings.{environment.EnvironmentName}.json");
            if (File.Exists(envPath))
            {
                files.Add(envPath);
            }
        }

        return files;
    }
}
