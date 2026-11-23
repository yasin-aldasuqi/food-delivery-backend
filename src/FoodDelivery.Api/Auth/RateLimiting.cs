using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace FoodDelivery.Api.Auth;

public class RateLimitRule
{
    public int PermitLimit { get; init; }
    public int WindowSeconds { get; init; }

    public bool IsValid => PermitLimit >= 1 && WindowSeconds >= 1;
}

public class RateLimitingOptions
{
    public const string SectionName = "RateLimiting";

    public RateLimitRule Login { get; init; } = new() { PermitLimit = 5, WindowSeconds = 60 };
    public RateLimitRule Register { get; init; } = new() { PermitLimit = 5, WindowSeconds = 3600 };
}

public static class RateLimitPolicies
{
    public const string Login = "login";
    public const string Register = "register";

    // Sliding windows are split into this many segments (e.g. 60s -> six 10s segments).
    private const int SegmentsPerWindow = 6;

    public static IServiceCollection AddAuthRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        // ValidateDataAnnotations doesn't look inside nested objects (Login/Register),
        // so the rules are validated explicitly.
        services.AddOptions<RateLimitingOptions>()
            .Bind(configuration.GetSection(RateLimitingOptions.SectionName))
            .Validate(o => o.Login.IsValid && o.Register.IsValid,
                "RateLimiting: PermitLimit and WindowSeconds must be at least 1.")
            .ValidateOnStart();

        var limits = configuration.GetSection(RateLimitingOptions.SectionName).Get<RateLimitingOptions>()
                     ?? new RateLimitingOptions();

        services.AddRateLimiter(options =>
        {
            options.AddPolicy(Login, context => SlidingWindowPerClientIp(context, limits.Login));
            options.AddPolicy(Register, context => SlidingWindowPerClientIp(context, limits.Register));

            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = OnRejectedAsync;
        });

        return services;
    }

    // One independent counter per client IP.
    // Behind a reverse proxy, configure ForwardedHeaders with the trusted proxies so
    // RemoteIpAddress is the real client. Never read X-Forwarded-For directly here:
    // clients can put anything in it and bypass the limit.
    private static RateLimitPartition<string> SlidingWindowPerClientIp(HttpContext context, RateLimitRule rule) =>
        RateLimitPartition.GetSlidingWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = rule.PermitLimit,
                Window = TimeSpan.FromSeconds(rule.WindowSeconds),
                SegmentsPerWindow = SegmentsPerWindow,
                QueueLimit = 0 // reject immediately; queueing would hold connections open
            });

    private static async ValueTask OnRejectedAsync(OnRejectedContext context, CancellationToken ct)
    {
        var http = context.HttpContext;

        var retryAfter = GetRetryAfter(context);
        if (retryAfter is not null)
        {
            http.Response.Headers.RetryAfter =
                ((int)Math.Ceiling(retryAfter.Value.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        }

        http.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(RateLimitPolicies))
            .LogWarning("Rate limit exceeded for {Path} from {ClientIp}",
                http.Request.Path, http.Connection.RemoteIpAddress);

        await Results.Problem(
                statusCode: StatusCodes.Status429TooManyRequests,
                title: "Too many requests",
                detail: "Too many attempts. Wait before trying again (see the Retry-After header).")
            .ExecuteAsync(http);
    }

    private static TimeSpan? GetRetryAfter(OnRejectedContext context)
    {
        // Fixed-window and token-bucket limiters report this; the sliding-window limiter doesn't.
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var reported))
            return reported;

        // Sliding window: permits are freed when a segment slides out of the window,
        // so the earliest a retry can succeed is about one segment from now.
        var policy = context.HttpContext.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName;
        var limits = context.HttpContext.RequestServices.GetRequiredService<IOptions<RateLimitingOptions>>().Value;
        var rule = policy switch
        {
            Login => limits.Login,
            Register => limits.Register,
            _ => null
        };

        return rule is null ? null : TimeSpan.FromSeconds((double)rule.WindowSeconds / SegmentsPerWindow);
    }
}
