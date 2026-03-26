// LogicFlow.Scraper — GitHub Repository Analyzer
// Proprietary implementation by DelgadoLogic.Tech
// Mines open-source optimization repos for technique analysis (NOT code copying)

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace LogicFlow.Scraper;

/// <summary>
/// Analyzes GitHub repositories for Windows optimization techniques.
/// Extracts technique patterns and architectural insights — never copies code.
/// </summary>
public sealed class RepoAnalyzer
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<RepoAnalyzer> _logger;

    private static readonly RepoTarget[] Targets =
    [
        new("memstechtips", "Winhance", "C# debloat/optimize/customize suite"),
        new("hellzerg", "optimizer", "Privacy/security/performance config utility"),
        new("Raphire", "Win11Debloat", "PowerShell telemetry/bloatware removal"),
        new("builtbybel", "TidyOS", "Integrated Windows cleanup tool"),
        new("LeDragoX", "Win-Debloat-Tools", "Minimal OS transformation scripts"),
    ];

    public RepoAnalyzer(HttpClient httpClient, ILogger<RepoAnalyzer> logger)
    {
        _httpClient = httpClient;
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "LogicFlow-Scraper/1.0");
        _logger = logger;
    }

    /// <summary>
    /// Analyzes all target repositories and generates a technique report.
    /// </summary>
    public async Task<List<RepoInsight>> AnalyzeAllAsync(CancellationToken ct = default)
    {
        var insights = new List<RepoInsight>();

        foreach (var target in Targets)
        {
            try
            {
                var insight = await AnalyzeRepoAsync(target, ct);
                insights.Add(insight);
                _logger.LogInformation("Analyzed repo: {Owner}/{Name}", target.Owner, target.Name);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to analyze {Owner}/{Name}", target.Owner, target.Name);
            }
        }

        return insights;
    }

    private async Task<RepoInsight> AnalyzeRepoAsync(RepoTarget target, CancellationToken ct)
    {
        var apiUrl = $"https://api.github.com/repos/{target.Owner}/{target.Name}";
        var response = await _httpClient.GetStringAsync(apiUrl, ct);
        var repoData = JsonDocument.Parse(response);
        var root = repoData.RootElement;

        return new RepoInsight
        {
            Owner = target.Owner,
            Name = target.Name,
            Description = target.Description,
            Stars = root.TryGetProperty("stargazers_count", out var s) ? s.GetInt32() : 0,
            Language = root.TryGetProperty("language", out var l) ? l.GetString() ?? "" : "",
            LastUpdated = root.TryGetProperty("updated_at", out var u)
                ? DateTimeOffset.Parse(u.GetString()!) : DateTimeOffset.MinValue,
            Topics = root.TryGetProperty("topics", out var t)
                ? t.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : [],
            AnalyzedAt = DateTimeOffset.UtcNow,
            Techniques = InferTechniques(target.Description)
        };
    }

    /// <summary>
    /// Infers optimization techniques from repo description and known patterns.
    /// This is INTENT analysis, not code extraction.
    /// </summary>
    private static List<string> InferTechniques(string description)
    {
        var techniques = new List<string>();
        var lower = description.ToLowerInvariant();

        if (lower.Contains("debloat")) techniques.Add("UWP/MSIX package removal via PackageManager API");
        if (lower.Contains("optimize")) techniques.Add("Windows service optimization via SCManager");
        if (lower.Contains("privacy")) techniques.Add("Telemetry endpoint blocking via firewall rules");
        if (lower.Contains("security")) techniques.Add("Security policy hardening via Group Policy API");
        if (lower.Contains("driver")) techniques.Add("Hardware ID matching via Win32_PnPEntity WMI");
        if (lower.Contains("cleanup")) techniques.Add("Temp/cache cleanup via DirectoryInfo enumeration");
        if (lower.Contains("customize")) techniques.Add("Registry-based UI customization");

        return techniques;
    }
}

// ─── Data Models ───────────────────────────────────────────────
public sealed record RepoTarget(string Owner, string Name, string Description);

public sealed class RepoInsight
{
    public string Owner { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public int Stars { get; set; }
    public string Language { get; set; } = "";
    public DateTimeOffset LastUpdated { get; set; }
    public List<string> Topics { get; set; } = [];
    public DateTimeOffset AnalyzedAt { get; set; }
    public List<string> Techniques { get; set; } = [];
}
