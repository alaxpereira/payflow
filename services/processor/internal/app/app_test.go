package app

import (
	"testing"
	"time"
)

func TestConfigFromEnv_ExigeVariaveisObrigatorias(t *testing.T) {
	t.Setenv("PROCESSOR_DATABASE_URL", "")
	t.Setenv("PROCESSOR_RABBITMQ_URL", "")

	if _, err := ConfigFromEnv(); err == nil {
		t.Fatal("esperado erro com variáveis obrigatórias ausentes")
	}
}

func TestConfigFromEnv_LeAtrasosDeRetentativa(t *testing.T) {
	t.Setenv("PROCESSOR_DATABASE_URL", "postgres://localhost/processor")
	t.Setenv("PROCESSOR_RABBITMQ_URL", "amqp://localhost")
	t.Setenv("PROCESSOR_HTTP_ADDR", "")
	t.Setenv("PROCESSOR_RETRY_DELAYS", "500ms, 5s,1m")

	cfg, err := ConfigFromEnv()
	if err != nil {
		t.Fatalf("erro inesperado: %v", err)
	}

	want := []time.Duration{500 * time.Millisecond, 5 * time.Second, time.Minute}
	if len(cfg.Retry.Delays) != len(want) {
		t.Fatalf("Delays = %v, esperado %v", cfg.Retry.Delays, want)
	}
	for i := range want {
		if cfg.Retry.Delays[i] != want[i] {
			t.Errorf("Delays[%d] = %v, esperado %v", i, cfg.Retry.Delays[i], want[i])
		}
	}
	if cfg.HTTPAddr != ":8081" {
		t.Errorf("HTTPAddr padrão = %q, esperado :8081", cfg.HTTPAddr)
	}
}

func TestConfigFromEnv_RejeitaAtrasoInvalido(t *testing.T) {
	t.Setenv("PROCESSOR_DATABASE_URL", "postgres://localhost/processor")
	t.Setenv("PROCESSOR_RABBITMQ_URL", "amqp://localhost")
	t.Setenv("PROCESSOR_RETRY_DELAYS", "1s,depois")

	if _, err := ConfigFromEnv(); err == nil {
		t.Fatal("esperado erro com atraso inválido")
	}
}
