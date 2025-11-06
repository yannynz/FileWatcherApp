namespace FileWatcherApp.Services.FileWatcher;

/// <summary>
/// Options used to configure directory monitoring and queue publishing.
/// </summary>
public sealed class FileWatcherOptions
{
    /// <summary>Gets or sets the directory observed for laser events.</summary>
    public string? LaserDirectory { get; set; }

    /// <summary>Gets or sets the directory observed for processed facas.</summary>
    public string? FacasDirectory { get; set; }

    /// <summary>Gets or sets the Dobradeira directory used to resolve final DXF files.</summary>
    public string? DobrasDirectory { get; set; }

    /// <summary>Gets or sets the directory that receives OP PDFs.</summary>
    public string? OpsDirectory { get; set; }

    /// <summary>Gets or sets the optional override for the DESTACADOR subfolder name.</summary>
    public string DestacadorSubfolderName { get; set; } = "DESTACADOR";

    /// <summary>Gets or sets the logical queue names used for legacy notifications.</summary>
    public Dictionary<string, string> QueueNames { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Laser"] = "laser_notifications",
        ["Facas"] = "facas_notifications",
        ["Dobra"] = "dobra_notifications",
        ["Ops"] = "op.imported"
    };

    /// <summary>Gets or sets the DXF analysis request queue name.</summary>
    public string AnalysisRequestQueue { get; set; } = "facas.analysis.request";

    /// <summary>Gets or sets the optional DXF analysis exchange name.</summary>
    public string AnalysisRequestExchange { get; set; } = string.Empty;

    /// <summary>Gets or sets the debounce interval applied to file system events.</summary>
    public double DebounceIntervalMilliseconds { get; set; } = 1200;

    /// <summary>Gets or sets a value indicating whether the DXF analysis pipeline should be invoked.</summary>
    public bool EnableAnalysisPublishing { get; set; } = true;
}
