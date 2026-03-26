// LogicFlow.Commerce — PayPal Subscription Engine
// Proprietary implementation by DelgadoLogic.Tech
// Recurring billing via PayPal REST Subscriptions API

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace LogicFlow.Commerce;

/// <summary>
/// PayPal subscription service for LogicFlow Pro/ProFamily plans.
/// Pricing: Pro $9.99/mo ($79.99/yr) | ProFamily $14.99/mo ($119.99/yr)
/// </summary>
public sealed class PayPalSubscriptionService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<PayPalSubscriptionService> _logger;
    private readonly PayPalConfig _config;
    private string? _accessToken;
    private DateTimeOffset _tokenExpiry;

    public PayPalSubscriptionService(HttpClient httpClient, ILogger<PayPalSubscriptionService> logger, PayPalConfig config)
    {
        _httpClient = httpClient;
        _logger = logger;
        _config = config;
    }

    private async Task EnsureAuthenticatedAsync(CancellationToken ct)
    {
        if (_accessToken != null && _tokenExpiry > DateTimeOffset.UtcNow) return;

        var authStr = Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes($"{_config.ClientId}:{_config.ClientSecret}"));

        var request = new HttpRequestMessage(HttpMethod.Post, $"{_config.BaseUrl}/v1/oauth2/token");
        request.Headers.Authorization = new("Basic", authStr);
        request.Content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("grant_type", "client_credentials") });

        var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        var token = JsonDocument.Parse(json).RootElement;

        _accessToken = token.GetProperty("access_token").GetString();
        _tokenExpiry = DateTimeOffset.UtcNow.AddSeconds(token.GetProperty("expires_in").GetInt32() - 60);
    }

    /// <summary>
    /// Creates a PayPal catalog product for LogicFlow.
    /// </summary>
    public async Task<string> CreateProductAsync(CancellationToken ct = default)
    {
        await EnsureAuthenticatedAsync(ct);
        var product = new
        {
            name = "LogicFlow Pro",
            description = "AI-Powered Windows Optimization & Data Recovery Suite",
            type = "SOFTWARE",
            category = "SOFTWARE"
        };

        var request = new HttpRequestMessage(HttpMethod.Post, $"{_config.BaseUrl}/v1/catalogs/products");
        request.Headers.Authorization = new("Bearer", _accessToken);
        request.Content = new StringContent(JsonSerializer.Serialize(product), System.Text.Encoding.UTF8, "application/json");

        var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return result.RootElement.GetProperty("id").GetString() ?? "";
    }

    /// <summary>
    /// Creates subscription plans (Monthly/Annual for Pro and ProFamily).
    /// </summary>
    public async Task<string> CreatePlanAsync(string productId, SubscriptionPlan plan, CancellationToken ct = default)
    {
        await EnsureAuthenticatedAsync(ct);
        var paypalPlan = new
        {
            product_id = productId,
            name = plan.Name,
            description = plan.Description,
            billing_cycles = new[]
            {
                new  // Trial period
                {
                    frequency = new { interval_unit = "DAY", interval_count = 14 },
                    tenure_type = "TRIAL",
                    sequence = 1,
                    total_cycles = 1,
                    pricing_scheme = new { fixed_price = new { value = "0", currency_code = "USD" } }
                },
                new // Regular billing
                {
                    frequency = new { interval_unit = plan.IntervalUnit, interval_count = plan.IntervalCount },
                    tenure_type = "REGULAR",
                    sequence = 2,
                    total_cycles = 0,
                    pricing_scheme = new { fixed_price = new { value = plan.Price.ToString("F2"), currency_code = "USD" } }
                }
            },
            payment_preferences = new
            {
                auto_bill_outstanding = true,
                setup_fee_failure_action = "CONTINUE",
                payment_failure_threshold = 3
            }
        };

        var request = new HttpRequestMessage(HttpMethod.Post, $"{_config.BaseUrl}/v1/billing/plans");
        request.Headers.Authorization = new("Bearer", _accessToken);
        request.Content = new StringContent(JsonSerializer.Serialize(paypalPlan), System.Text.Encoding.UTF8, "application/json");

        var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var planId = result.RootElement.GetProperty("id").GetString() ?? "";
        _logger.LogInformation("Created plan: {Name} ({Id}) at ${Price}", plan.Name, planId, plan.Price);
        return planId;
    }

    /// <summary>
    /// Returns the standard LogicFlow subscription plans.
    /// </summary>
    public static List<SubscriptionPlan> GetStandardPlans() =>
    [
        new("LogicFlow Pro Monthly",    "Full suite, 1 PC — billed monthly",   9.99m,  "MONTH", 1),
        new("LogicFlow Pro Annual",     "Full suite, 1 PC — billed annually",  79.99m, "YEAR",  1),
        new("LogicFlow Pro Family Monthly", "Full suite, 5 PCs — billed monthly",  14.99m, "MONTH", 1),
        new("LogicFlow Pro Family Annual",  "Full suite, 5 PCs — billed annually", 119.99m,"YEAR",  1),
    ];
}

public sealed class WebhookHandler
{
    private readonly ILogger<WebhookHandler> _logger;
    public WebhookHandler(ILogger<WebhookHandler> logger) => _logger = logger;

    public WebhookResult ProcessEvent(string eventType, string resourceId)
    {
        _logger.LogInformation("PayPal webhook: {Type} for {Id}", eventType, resourceId);
        return eventType switch
        {
            "BILLING.SUBSCRIPTION.ACTIVATED" => new(true, "Subscription activated"),
            "BILLING.SUBSCRIPTION.CANCELLED" => new(true, "Subscription cancelled"),
            "BILLING.SUBSCRIPTION.EXPIRED" => new(true, "Subscription expired"),
            "PAYMENT.SALE.COMPLETED" => new(true, "Payment received"),
            "PAYMENT.SALE.DENIED" => new(true, "Payment denied"),
            _ => new(false, $"Unhandled event: {eventType}")
        };
    }
}

// ─── Data Models ───
public sealed class PayPalConfig
{
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string BaseUrl { get; set; } = "https://api-m.sandbox.paypal.com";
    public string WebhookId { get; set; } = "";
}

public sealed record SubscriptionPlan(string Name, string Description, decimal Price, string IntervalUnit, int IntervalCount);
public sealed record WebhookResult(bool Handled, string Message);
