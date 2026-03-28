// ─────────────────────────────────────────────────────────────────────────────
// DelgadoLogic.Core — Product Manifest
// Central registry of all DelgadoLogic products and version metadata.
// Modeled after Adobe's Creative Cloud product catalog and Microsoft's
// Office shared component registration system.
// ─────────────────────────────────────────────────────────────────────────────

namespace DelgadoLogic.Core;

/// <summary>
/// Identifies a DelgadoLogic product. Add new products here to register
/// them with the shared runtime — licensing, telemetry, and updates all
/// flow through this manifest.
/// </summary>
public enum DelgadoProduct
{
    /// <summary>LogicFlow — Windows system optimizer and performance suite.</summary>
    LogicFlow,

    /// <summary>Aeon Browser — sovereign Chromium-based privacy browser.</summary>
    AeonBrowser,

    /// <summary>Reserved for future DelgadoLogic products.</summary>
    Reserved3,
    Reserved4
}

/// <summary>
/// Runtime metadata for the currently running DelgadoLogic product.
/// Each product sets this once at startup via <see cref="ProductManifest.Register"/>.
/// </summary>
public sealed class ProductManifest
{
    private static ProductManifest? _current;

    /// <summary>The active product's manifest. Set at startup via Register().</summary>
    public static ProductManifest Current => _current
        ?? throw new InvalidOperationException(
            "ProductManifest.Register() must be called at application startup.");

    public DelgadoProduct Product { get; private init; }
    public string DisplayName { get; private init; } = "";
    public string Version { get; private init; } = "";
    public string AppDataPath { get; private init; } = "";
    public DateTimeOffset RegisteredAt { get; private init; }

    // Well-known display names per product
    private static readonly Dictionary<DelgadoProduct, string> DisplayNames = new()
    {
        [DelgadoProduct.LogicFlow]    = "LogicFlow",
        [DelgadoProduct.AeonBrowser]  = "Aeon Browser",
    };

    /// <summary>
    /// Called once at application startup. Registers this product with the
    /// DelgadoLogic shared runtime and sets up the AppData directory structure.
    /// </summary>
    /// <param name="product">Which product is starting up.</param>
    /// <param name="version">Semantic version string, e.g. "1.0.0".</param>
    public static ProductManifest Register(DelgadoProduct product, string version)
    {
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DelgadoLogic",
            product.ToString());

        Directory.CreateDirectory(appData);

        _current = new ProductManifest
        {
            Product        = product,
            DisplayName    = DisplayNames.GetValueOrDefault(product, product.ToString()),
            Version        = version,
            AppDataPath    = appData,
            RegisteredAt   = DateTimeOffset.UtcNow
        };

        return _current;
    }

    /// <summary>
    /// Returns the shared DelgadoLogic AppData root — all products can read from here
    /// for cross-product features like shared licensing and identity.
    /// </summary>
    public static string SharedAppDataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DelgadoLogic",
        "Shared");

    public override string ToString() =>
        $"{DisplayName} v{Version} ({Product}) — AppData: {AppDataPath}";
}
