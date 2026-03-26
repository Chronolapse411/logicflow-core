# LogicFlow API Reference

## Architecture

LogicFlow is built as a modular .NET 8 solution with 9 independent projects:

```
LogicFlow.Core       → Shared kernel (logging, WMI events, system profiling)
LogicFlow.Scraper    → Research intelligence gathering
LogicFlow.Sentinel   → Security scanning and privacy scrubbing
LogicFlow.Guardian   → Performance optimization and driver management
LogicFlow.Lazarus    → Deep-sector data recovery
LogicFlow.Registry   → Registry analysis and repair
LogicFlow.Licensing  → RSA license validation and HWID management
LogicFlow.Commerce   → PayPal subscription engine
LogicFlow.Dashboard  → WPF glassmorphism UI
```

## Core Services

### EventTriggerService
WMI event subscription manager. Monitors process creation, service changes, USB insertion.
```csharp
var triggers = new EventTriggerService(logger);
triggers.RegisterDefaultEvents();
triggers.OnSystemEvent += (s, e) => Console.WriteLine($"Event: {e.EventName}");
```

### SystemProfiler
Hardware fingerprinting and real-time system telemetry.
```csharp
var profiler = new SystemProfiler(logger);
var snapshot = profiler.CaptureSnapshot();
// snapshot.Cpu, .Memory, .Gpu, .Os, .Disks, .Motherboard
```

## Module APIs

### Sentinel
```csharp
var scanner = new VulnerabilityScanner(logger);
var result = await scanner.ScanAsync();
// result.Vulnerabilities — known problematic KBs
// result.ExposedServices — risky running services
// result.TelemetryStatus — current telemetry level

var scrubber = new PrivacyScrubber(logger);
var report = scrubber.Scrub();
// report.TotalBytesCleared, .TotalFilesCleared
```

### Guardian
```csharp
var drivers = new SmartDriverEngine(logger);
var reports = drivers.ScanDrivers();
// reports[i].Status = Healthy|Outdated|Missing|Unsigned

var debloat = new DebloatEngine(logger);
var bloatware = debloat.ScanBloatware();

var startup = new StartupOptimizer(logger);
var items = startup.AnalyzeStartupItems();
// items sorted by ImpactScore (1-10)

var power = new PowerPlanAutomation(logger);
bool hasNpu = power.HasNeuralProcessor();
```

### Lazarus
```csharp
var scanner = new SectorScanner(logger);
scanner.OpenDrive(0); // PhysicalDrive0
await foreach (var chunk in scanner.ScanRangeAsync(0, 1000000))
{
    var carver = new FileCarver(logger);
    var headers = carver.ScanBuffer(chunk.Data, chunk.ByteOffset);
    // headers[i].Extension, .FileType, .ByteOffset
}

var mtp = new MtpBridge(logger);
var devices = mtp.EnumerateDevices();
```

### Registry
```csharp
var analyzer = new RegistryAnalyzer(logger);
var result = analyzer.Scan();
// result.OrphanedSoftwareKeys, .BrokenFileAssociations,
// .InvalidPaths, .ObsoleteRunEntries
```

### Licensing
```csharp
var hwid = new HwidGenerator().GenerateHwid();
var validator = new RsaLicenseValidator(logger, publicKeyXml);
var validation = validator.Validate(new LicenseToken(payload, signature));

var trial = new TrialManager(logger, appDataPath);
var status = trial.GetStatus();
// status.IsActive, .DaysRemaining
```

### Commerce
```csharp
var plans = PayPalSubscriptionService.GetStandardPlans();
// Pro Monthly: $9.99, Pro Annual: $79.99
// Family Monthly: $14.99, Family Annual: $119.99

var service = new PayPalSubscriptionService(httpClient, logger, config);
var productId = await service.CreateProductAsync();
var planId = await service.CreatePlanAsync(productId, plans[0]);
```

## Pricing Tiers

| Tier | Monthly | Annual | Devices |
|------|---------|--------|---------|
| Free | $0 | $0 | 1 |
| Pro | $9.99 | $79.99 | 1 |
| Pro Family | $14.99 | $119.99 | 5 |
| Enterprise | Custom | Custom | Unlimited |
