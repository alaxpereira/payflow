// Package telemetry configura o OpenTelemetry (traces e métricas) do processor.
package telemetry

import (
	"context"
	"errors"
	"fmt"
	"os"
	"time"

	"go.opentelemetry.io/otel"
	"go.opentelemetry.io/otel/attribute"
	"go.opentelemetry.io/otel/exporters/otlp/otlpmetric/otlpmetrichttp"
	"go.opentelemetry.io/otel/exporters/otlp/otlptrace/otlptracehttp"
	"go.opentelemetry.io/otel/propagation"
	sdkmetric "go.opentelemetry.io/otel/sdk/metric"
	"go.opentelemetry.io/otel/sdk/resource"
	sdktrace "go.opentelemetry.io/otel/sdk/trace"
)

const ServiceName = "payflow-processor"

// Setup registra os providers globais. A propagação W3C fica sempre ativa, para o contexto que vem do ledger
// seguir adiante; a exportação só liga quando OTEL_EXPORTER_OTLP_TRACES_ENDPOINT e
// OTEL_EXPORTER_OTLP_METRICS_ENDPOINT estão definidas (ex.: http://jaeger:4318/v1/traces).
func Setup(ctx context.Context) (shutdown func(context.Context) error, err error) {
	otel.SetTextMapPropagator(propagation.NewCompositeTextMapPropagator(propagation.TraceContext{}, propagation.Baggage{}))

	res, err := resource.Merge(resource.Default(),
		resource.NewSchemaless(attribute.String("service.name", ServiceName)))
	if err != nil {
		return nil, fmt.Errorf("montando resource: %w", err)
	}

	var shutdowns []func(context.Context) error

	if os.Getenv("OTEL_EXPORTER_OTLP_TRACES_ENDPOINT") != "" {
		exporter, err := otlptracehttp.New(ctx)
		if err != nil {
			return nil, fmt.Errorf("criando exportador de traces: %w", err)
		}
		provider := sdktrace.NewTracerProvider(sdktrace.WithBatcher(exporter), sdktrace.WithResource(res))
		otel.SetTracerProvider(provider)
		shutdowns = append(shutdowns, provider.Shutdown)
	}

	if os.Getenv("OTEL_EXPORTER_OTLP_METRICS_ENDPOINT") != "" {
		exporter, err := otlpmetrichttp.New(ctx)
		if err != nil {
			return nil, fmt.Errorf("criando exportador de métricas: %w", err)
		}
		provider := sdkmetric.NewMeterProvider(
			sdkmetric.WithReader(sdkmetric.NewPeriodicReader(exporter, sdkmetric.WithInterval(10*time.Second))),
			sdkmetric.WithResource(res),
		)
		otel.SetMeterProvider(provider)
		shutdowns = append(shutdowns, provider.Shutdown)
	}

	return func(ctx context.Context) error {
		var errs []error
		for _, shutdown := range shutdowns {
			errs = append(errs, shutdown(ctx))
		}
		return errors.Join(errs...)
	}, nil
}
