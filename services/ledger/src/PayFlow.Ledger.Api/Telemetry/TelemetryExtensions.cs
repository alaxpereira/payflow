using Npgsql;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using PayFlow.Ledger.Application.Telemetry;

namespace PayFlow.Ledger.Api.Telemetry;

internal static class TelemetryExtensions
{
    public const string ServiceName = "payflow-ledger";

    /// <summary>
    /// Traces e métricas são sempre coletados (os testes dependem do contexto de trace); a exportação só liga
    /// quando os endpoints OTLP estão configurados.
    /// </summary>
    public static WebApplicationBuilder AddTelemetry(this WebApplicationBuilder builder)
    {
        var settings = builder.Configuration.GetSection(TelemetryOptions.SectionName).Get<TelemetryOptions>()
            ?? new TelemetryOptions();

        builder.Logging.Configure(options =>
            options.ActivityTrackingOptions = ActivityTrackingOptions.TraceId | ActivityTrackingOptions.SpanId);

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(ServiceName))
            .WithTracing(tracing =>
            {
                tracing
                    .AddSource(LedgerTelemetry.ActivitySourceName)
                    .AddAspNetCoreInstrumentation(options =>
                        options.Filter = context => !context.Request.Path.StartsWithSegments("/health"))
                    .AddNpgsql();

                if (settings.TracesEndpoint is { } endpoint)
                    tracing.AddOtlpExporter(otlp => UseHttp(otlp, endpoint));
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddMeter(LedgerTelemetry.MeterName)
                    .AddAspNetCoreInstrumentation()
                    .AddRuntimeInstrumentation();

                if (settings.MetricsEndpoint is { } endpoint)
                {
                    metrics.AddOtlpExporter((otlp, reader) =>
                    {
                        UseHttp(otlp, endpoint);
                        reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds =
                            (int)settings.MetricsExportInterval.TotalMilliseconds;
                    });
                }
            });

        return builder;
    }

    // Com HTTP, o endpoint configurado em código é usado como está, sem acrescentar /v1/traces ou /v1/metrics.
    private static void UseHttp(OtlpExporterOptions options, Uri endpoint)
    {
        options.Endpoint = endpoint;
        options.Protocol = OtlpExportProtocol.HttpProtobuf;
    }
}

internal sealed class TelemetryOptions
{
    public const string SectionName = "Telemetry";

    /// <summary>Ex.: http://jaeger:4318/v1/traces</summary>
    public Uri? TracesEndpoint { get; set; }

    /// <summary>Ex.: http://prometheus:9090/api/v1/otlp/v1/metrics</summary>
    public Uri? MetricsEndpoint { get; set; }

    public TimeSpan MetricsExportInterval { get; set; } = TimeSpan.FromSeconds(10);
}
