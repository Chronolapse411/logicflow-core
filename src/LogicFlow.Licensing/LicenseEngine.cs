// LogicFlow.Licensing — RSA License Validator & HWID Manager
// Proprietary implementation by DelgadoLogic.Tech

using System.Security.Cryptography;
using System.Management;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace LogicFlow.Licensing;

public sealed class HwidGenerator
{
    public string GenerateHwid()
    {
        var components = new StringBuilder();
        components.Append(GetWmi("Win32_Processor", "ProcessorId"));
        components.Append(GetWmi("Win32_BaseBoard", "SerialNumber"));
        components.Append(GetWmi("Win32_BIOS", "SerialNumber"));
        components.Append(GetWmi("Win32_DiskDrive", "SerialNumber"));
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(components.ToString())));
    }

    private static string GetWmi(string cls, string prop)
    {
        try
        {
            using var s = new ManagementObjectSearcher($"SELECT {prop} FROM {cls}");
            foreach (var o in s.Get()) return o[prop]?.ToString()?.Trim() ?? "";
        } catch { }
        return "";
    }
}

public sealed class RsaLicenseValidator
{
    private readonly RSA _publicKey;
    private readonly ILogger<RsaLicenseValidator> _logger;

    public RsaLicenseValidator(ILogger<RsaLicenseValidator> logger, string? publicKeyXml = null)
    {
        _logger = logger;
        _publicKey = RSA.Create(2048);
        if (publicKeyXml != null) _publicKey.FromXmlString(publicKeyXml);
    }

    public static (string PublicKey, string PrivateKey) GenerateKeyPair()
    {
        using var rsa = RSA.Create(2048);
        return (rsa.ToXmlString(false), rsa.ToXmlString(true));
    }

    public LicenseValidation Validate(LicenseToken token)
    {
        try
        {
            var payloadBytes = Encoding.UTF8.GetBytes(token.Payload);
            var sigBytes = Convert.FromBase64String(token.Signature);
            if (!_publicKey.VerifyData(payloadBytes, sigBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                return new(false, "Invalid signature", LicenseTier.Free);

            var claims = JsonSerializer.Deserialize<LicenseClaims>(token.Payload);
            if (claims is null) return new(false, "Corrupt payload", LicenseTier.Free);
            if (claims.ExpiresAt < DateTimeOffset.UtcNow) return new(false, "Expired", LicenseTier.Free);

            var hwid = new HwidGenerator().GenerateHwid();
            if (claims.BoundHwid != hwid) return new(false, "HWID mismatch", LicenseTier.Free);

            return new(true, "Valid", claims.Tier);
        }
        catch (Exception ex) { return new(false, ex.Message, LicenseTier.Free); }
    }
}

public sealed class TrialManager
{
    private readonly string _trialFile;
    private readonly ILogger<TrialManager> _logger;

    public TrialManager(ILogger<TrialManager> logger, string appDataPath)
    {
        _logger = logger;
        Directory.CreateDirectory(Path.Combine(appDataPath, "License"));
        _trialFile = Path.Combine(appDataPath, "License", ".trial");
    }

    public TrialStatus GetStatus()
    {
        if (!File.Exists(_trialFile)) return InitTrial();
        var data = JsonSerializer.Deserialize<TrialData>(
            Encoding.UTF8.GetString(Convert.FromBase64String(File.ReadAllText(_trialFile))));
        if (data is null) return InitTrial();
        var days = Math.Max(0, 14 - (int)(DateTimeOffset.UtcNow - data.StartedAt).TotalDays);
        return new(days > 0, days, 14, data.BoundHwid != new HwidGenerator().GenerateHwid());
    }

    private TrialStatus InitTrial()
    {
        var data = new TrialData { StartedAt = DateTimeOffset.UtcNow, BoundHwid = new HwidGenerator().GenerateHwid() };
        File.WriteAllText(_trialFile, Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data))));
        return new(true, 14, 14, false);
    }
}

public sealed record LicenseToken(string Payload, string Signature);
public sealed record LicenseValidation(bool IsValid, string Message, LicenseTier Tier);
public sealed class LicenseClaims
{
    public string Email { get; set; } = "";
    public string BoundHwid { get; set; } = "";
    public LicenseTier Tier { get; set; }
    public DateTimeOffset IssuedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LicenseTier { Free, Pro, ProFamily, Enterprise }

public sealed record TrialStatus(bool IsActive, int DaysRemaining, int TotalDays, bool IsTampered);
internal sealed class TrialData { public DateTimeOffset StartedAt { get; set; } public string BoundHwid { get; set; } = ""; }
