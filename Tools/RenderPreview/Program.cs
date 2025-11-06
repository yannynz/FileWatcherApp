using FileWatcherApp.Services.DXFAnalysis;
using FileWatcherApp.Services.DXFAnalysis.Models;
using FileWatcherApp.Services.DXFAnalysis.Geometry;
using FileWatcherApp.Services.DXFAnalysis.Rendering;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using netDxf;

var cwd = Directory.GetCurrentDirectory();
var configuration = new ConfigurationBuilder()
    .SetBasePath(cwd)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
    .AddJsonFile("appsettings.Production.json", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables()
    .Build();

var analysisOptions = new DXFAnalysisOptions();
configuration.GetSection("DXFAnalysis").Bind(analysisOptions);

analysisOptions.PersistLocalImageCopy = true;
if (string.IsNullOrWhiteSpace(analysisOptions.OutputImageFolder))
{
    analysisOptions.OutputImageFolder = Path.Combine(cwd, "artifacts", "renders");
}

analysisOptions.ImageStorage.Enabled = false;

var servicesLoggerFactory = LoggerFactory.Create(builder =>
{
    builder
        .SetMinimumLevel(LogLevel.Information)
        .AddConsole();
});

var options = Options.Create(analysisOptions);
var preprocessor = new DXFPreprocessor(options, servicesLoggerFactory.CreateLogger<DXFPreprocessor>());
var analyzer = new DXFAnalyzer(options, servicesLoggerFactory.CreateLogger<DXFAnalyzer>());
var calibratedRenderer = new CalibratedDxfRenderer(servicesLoggerFactory.CreateLogger<CalibratedDxfRenderer>());
var renderer = new DXFImageRenderer(options, servicesLoggerFactory.CreateLogger<DXFImageRenderer>(), calibratedRenderer);
var scorer = new ComplexityScorer(options, servicesLoggerFactory.CreateLogger<ComplexityScorer>());

var filePath = args.Length > 0 ? args[0] : "NR 120184.dxf";
filePath = Path.GetFullPath(filePath);

if (!File.Exists(filePath))
{
    Console.Error.WriteLine($"Arquivo DXF não encontrado: {filePath}");
    return 1;
}

var analysisId = Guid.NewGuid().ToString("N");
Console.WriteLine($"[Preview] Carregando DXF: {filePath}");

var document = LoadDocumentWithFallback(filePath);
var renderDocument = CloneForRender(document, filePath);
var quality = preprocessor.Preprocess(document);
var geometry = analyzer.Analyze(document, quality);
var scoreResult = scorer.Compute(geometry.Metrics);
var rendered = await renderer.RenderAsync(analysisId, filePath, geometry, scoreResult.Score, renderDocument);

var outputFolder = analysisOptions.OutputImageFolder ?? Path.Combine(cwd, "artifacts", "renders");
Directory.CreateDirectory(outputFolder);
var outputPath = Path.Combine(outputFolder, $"{Path.GetFileNameWithoutExtension(filePath)}_{analysisId}.png");
await File.WriteAllBytesAsync(outputPath, rendered.Data);

Console.WriteLine($"[Preview] Render salvo em {outputPath}");
Console.WriteLine($"[Preview] Dimensões: {rendered.WidthPx}x{rendered.HeightPx} px | DPI: {rendered.Dpi:0.##} | Score: {scoreResult.Score:0.###}");
return 0;

static DxfDocument LoadDocumentWithFallback(string path)
{
    try
    {
        return DxfDocument.Load(path);
    }
    catch (netDxf.IO.DxfVersionNotSupportedException)
    {
        var upgraded = TryLoadWithHeaderUpgrade(path);
        if (upgraded is not null)
        {
            Console.WriteLine("[Preview] DXF recarregado com fallback para AutoCAD 2000.");
            return upgraded;
        }

        throw;
    }
}

static DxfDocument? TryLoadWithHeaderUpgrade(string path)
{
    try
    {
        var bytes = File.ReadAllBytes(path);
        var header = System.Text.Encoding.ASCII.GetBytes("AC1014");
        var index = IndexOf(bytes, header);
        if (index < 0)
        {
            return null;
        }

        var replacement = System.Text.Encoding.ASCII.GetBytes("AC1015");
        Buffer.BlockCopy(replacement, 0, bytes, index, replacement.Length);

        using var stream = new MemoryStream(bytes, writable: false);
        return DxfDocument.Load(stream);
    }
    catch
    {
        return null;
    }
}

static DxfDocument CloneForRender(DxfDocument source, string path) => LoadDocumentWithFallback(path);

static int IndexOf(byte[] source, byte[] pattern)
{
    for (int i = 0; i <= source.Length - pattern.Length; i++)
    {
        var match = true;
        for (int j = 0; j < pattern.Length; j++)
        {
            if (source[i + j] != pattern[j])
            {
                match = false;
                break;
            }
        }

        if (match)
        {
            return i;
        }
    }

    return -1;
}
