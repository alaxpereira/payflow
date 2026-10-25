using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace PayFlow.Ledger.Api.Security;

/// <summary>
/// Limite de requisições por IP, para a demo pública. Desligado por padrão (RateLimiting:PermitsPerMinute = 0).
/// Atrás de um proxy, o IP do cliente só é o real com ASPNETCORE_FORWARDEDHEADERS_ENABLED=true.
/// </summary>
internal static class RateLimitingExtensions
{
    public static IServiceCollection AddPerIpRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(_ => { });

        // Lido sob demanda, para os testes poderem ligar o limite sobrescrevendo a configuração.
        services.AddOptions<RateLimiterOptions>().Configure<IConfiguration>((options, configuration) =>
        {
            var permitsPerMinute = configuration.GetValue<int>("RateLimiting:PermitsPerMinute");

            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = (context, _) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
                }
                return ValueTask.CompletedTask;
            };

            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
                permitsPerMinute <= 0 || http.Request.Path.StartsWithSegments("/health")
                    ? RateLimitPartition.GetNoLimiter("sem-limite")
                    : RateLimitPartition.GetFixedWindowLimiter(
                        http.Connection.RemoteIpAddress?.ToString() ?? "desconhecido",
                        _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = permitsPerMinute,
                            Window = TimeSpan.FromMinutes(1),
                            QueueLimit = 0,
                        }));
        });

        return services;
    }
}
