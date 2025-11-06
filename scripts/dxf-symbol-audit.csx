#r "../bin/Debug/net8.0/FileWatcherApp.dll"
#r "nuget: Microsoft.Extensions.Configuration,9.0.8"
#r "nuget: Microsoft.Extensions.Configuration.Json,9.0.8"
#r "nuget: Microsoft.Extensions.Configuration.Binder,9.0.8"
#r "nuget: Microsoft.Extensions.Logging.Abstractions,10.0.0-preview.7.25380.108"
#r "nuget: netDxf,3.0.1"

using System;
using System.IO;
using FileWatcherApp.Services.DXFAnalysis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using netDxf;

if (Args.Length == 0)
{
    Console.WriteLine("Usage: dotnet script scripts/dxf-symbol-audit.csx -- <path-to-dxf> [appsettings.json]");
    return;
}

var dxfPath = Path.GetFullPath(Args[0]);
if (!File.Exists(dxfPath))
{
    Console.WriteLine($"DXF file not found: {dxfPath}");
    return;
}

var configPath = Args.Length > 1 ? Path.GetFullPath(Args[1]) : Path.GetFullPath("appsettings.json");
if (!File.Exists(configPath))
{
    Console.WriteLine($"Configuration file not found: {configPath}");
    return;
}

var configuration = new ConfigurationBuilder()
    .AddJsonFile(configPath, optional: false)
    .Build();

var options = new DXFAnalysisOptions();
configuration.GetSection("DXFAnalysis").Bind(options);

var optionsWrapper = Options.Create(options);
var preprocessor = new DXFPreprocessor(optionsWrapper, NullLogger<DXFPreprocessor>.Instance);
var analyzer = new DXFAnalyzer(optionsWrapper, NullLogger<DXFAnalyzer>.Instance);

Console.WriteLine($"Analyzing {dxfPath}...");
var document = DxfDocument.Load(dxfPath);
var quality = preprocessor.Preprocess(document);
var snapshot = analyzer.Analyze(document, quality);
var summary = snapshot.Metrics.Serrilha;

if (summary is null)
{
    Console.WriteLine("No serrilha symbols detected.");
    return;
}

Console.WriteLine($"Recognized symbols: {summary.TotalCount}");
Console.WriteLine($"Unknown symbols: {summary.UnknownCount}");

foreach (var entry in summary.Entries)
{
    Console.WriteLine($" - {entry.SemanticType} | count={entry.Count} | blade={entry.BladeCode ?? "n/a"}");
    if (entry.SymbolNames.Count > 0)
    {
        Console.WriteLine($"   Symbols: {string.Join(", ", entry.SymbolNames)}");
    }
    if (entry.EstimatedLength.HasValue)
    {
        Console.WriteLine($"   Length(mm): {entry.EstimatedLength.Value:0.###}");
    }
    if (entry.EstimatedToothCount.HasValue)
    {
        Console.WriteLine($"   ToothCount: {entry.EstimatedToothCount.Value:0.###}");
    }
}

if (summary.UnknownSymbols is { Count: > 0 })
{
    Console.WriteLine($"Unknown symbol names: {string.Join(", ", summary.UnknownSymbols)}");
}
