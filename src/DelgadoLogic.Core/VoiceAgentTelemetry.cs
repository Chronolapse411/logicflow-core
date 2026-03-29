// ─────────────────────────────────────────────────────────────────────────────
// VoiceAgentTelemetry.cs — Standardized Telemetry Models
//
// These classes represent the JSON payload schema that the various LogicFlow
// engines (Sentinel, System Optimizer, CyberShield) will pass directly into
// the LogicFlow.VoiceAgent. This provides the AI with absolute contextual
// awareness of the hardware and OS without hallucinations.
// ─────────────────────────────────────────────────────────────────────────────

using System.Text.Json;
using System.Text.Json.Serialization;

namespace DelgadoLogic.Core;

/// <summary>
/// The root context object that gets injected into the VoiceAgent's memory buffer.
/// </summary>
public sealed class SystemContextPayload
{
    [JsonPropertyName("identity")]
    public required IdentitySnapshot Identity { get; set; }

    [JsonPropertyName("hardware")]
    public required HardwareSnapshot Hardware { get; set; }

    [JsonPropertyName("security")]
    public required SecuritySnapshot Security { get; set; }

    [JsonPropertyName("performance")]
    public required PerformanceSnapshot Performance { get; set; }

    public string ToJson()
    {
        return JsonSerializer.Serialize(this, new JsonSerializerOptions 
        { 
            WriteIndented = true, 
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase 
        });
    }
}

public sealed class IdentitySnapshot
{
    [JsonPropertyName("deviceId")]
    public string DeviceId { get; set; } = "";
    
    [JsonPropertyName("activeLicenses")]
    public IReadOnlyList<string> ActiveLicenses { get; set; } = [];
}

public sealed class HardwareSnapshot
{
    [JsonPropertyName("cpuUsagePercent")]
    public double CpuUsagePercent { get; set; }
    
    [JsonPropertyName("ramAvailableMb")]
    public double RamAvailableMb { get; set; }

    [JsonPropertyName("activeTemperatures")]
    public IReadOnlyDictionary<string, double>? ActiveTemperatures { get; set; }
}

public sealed class SecuritySnapshot
{
    [JsonPropertyName("firewallStatus")]
    public string FirewallStatus { get; set; } = "Unknown";
    
    [JsonPropertyName("openPorts")]
    public IReadOnlyList<int> OpenPorts { get; set; } = [];
    
    [JsonPropertyName("activeThreats")]
    public int ActiveThreats { get; set; }
}

public sealed class PerformanceSnapshot
{
    [JsonPropertyName("bloatwareCount")]
    public int BloatwareCount { get; set; }
    
    [JsonPropertyName("registryAnomalies")]
    public int RegistryAnomalies { get; set; }
    
    [JsonPropertyName("uptimeSeconds")]
    public long UptimeSeconds { get; set; }
}
