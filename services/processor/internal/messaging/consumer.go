package messaging

import (
	"context"
	"errors"
	"fmt"
	"log/slog"
	"time"

	amqp "github.com/rabbitmq/amqp091-go"
)

const (
	retryCountHeader = "x-retry-count"
	lastErrorHeader  = "x-last-error"
	maxErrorLength   = 500
)

// HandlerFunc processa uma entrega. Erros marcados com Permanent vão direto para a DLQ; os demais são
// retentados com backoff.
type HandlerFunc func(ctx context.Context, delivery amqp.Delivery) error

type ConsumerOptions struct {
	Prefetch       int
	HandlerTimeout time.Duration
	MaxReconnect   time.Duration
}

// Consumer consome a fila principal com ack manual e reconecta sozinho quando a conexão cai.
type Consumer struct {
	url      string
	topology Topology
	handle   HandlerFunc
	opts     ConsumerOptions
	log      *slog.Logger
}

func NewConsumer(url string, topology Topology, handle HandlerFunc, opts ConsumerOptions, log *slog.Logger) *Consumer {
	if opts.Prefetch <= 0 {
		opts.Prefetch = 10
	}
	if opts.HandlerTimeout <= 0 {
		opts.HandlerTimeout = 30 * time.Second
	}
	if opts.MaxReconnect <= 0 {
		opts.MaxReconnect = 30 * time.Second
	}
	return &Consumer{url: url, topology: topology, handle: handle, opts: opts, log: log}
}

// Run consome até ctx ser cancelado. Mensagens em processamento no momento do cancelamento ficam sem ack e o
// broker as entrega de novo.
func (c *Consumer) Run(ctx context.Context) error {
	backoff := time.Second
	for {
		connected, err := c.consume(ctx)
		if ctx.Err() != nil {
			return nil
		}
		if connected {
			backoff = time.Second
		}

		c.log.Warn("conexão com o RabbitMQ perdida; reconectando", "erro", err, "em", backoff)
		select {
		case <-ctx.Done():
			return nil
		case <-time.After(backoff):
		}
		backoff = min(backoff*2, c.opts.MaxReconnect)
	}
}

func (c *Consumer) consume(ctx context.Context) (connected bool, err error) {
	conn, err := amqp.DialConfig(c.url, amqp.Config{Properties: amqp.Table{"connection_name": "payflow-processor"}})
	if err != nil {
		return false, fmt.Errorf("conectando: %w", err)
	}
	defer conn.Close()

	ch, err := conn.Channel()
	if err != nil {
		return false, fmt.Errorf("abrindo canal: %w", err)
	}
	defer ch.Close()

	if err := c.topology.Declare(ch); err != nil {
		return false, err
	}
	if err := ch.Qos(c.opts.Prefetch, 0, false); err != nil {
		return false, fmt.Errorf("configurando prefetch: %w", err)
	}
	// Confirms nas republicações: só damos ack na original depois que a cópia está segura no broker.
	if err := ch.Confirm(false); err != nil {
		return false, fmt.Errorf("ativando publisher confirms: %w", err)
	}

	deliveries, err := ch.ConsumeWithContext(ctx, c.topology.Queue, "payflow-processor", false, false, false, false, nil)
	if err != nil {
		return false, fmt.Errorf("iniciando consumo: %w", err)
	}

	closed := conn.NotifyClose(make(chan *amqp.Error, 1))
	c.log.Info("consumindo mensagens", "fila", c.topology.Queue)

	for {
		select {
		case <-ctx.Done():
			return true, nil
		case amqpErr := <-closed:
			if amqpErr == nil {
				return true, errors.New("conexão encerrada")
			}
			return true, amqpErr
		case delivery, ok := <-deliveries:
			if !ok {
				return true, errors.New("canal de consumo encerrado")
			}
			c.process(ctx, ch, delivery)
		}
	}
}

func (c *Consumer) process(ctx context.Context, ch *amqp.Channel, d amqp.Delivery) {
	retries := retryCount(d.Headers)
	log := c.log.With("message_id", d.MessageId, "type", d.Type, "retries", retries)

	handlerCtx, cancel := context.WithTimeout(ctx, c.opts.HandlerTimeout)
	err := c.handle(handlerCtx, d)
	cancel()

	decision := c.topology.Retry.Decide(err, retries)
	switch decision.Action {
	case ActionAck:
		ack(log, d)

	case ActionRetry:
		log.Warn("falha ao processar; nova tentativa agendada", "erro", err, "atraso", decision.Delay)
		c.forward(ctx, ch, d, c.topology.RetryQueue(decision.Delay), decision.Retries, err, log)

	case ActionDeadLetter:
		log.Error("mensagem enviada para a DLQ", "erro", err, "permanente", IsPermanent(err))
		c.forward(ctx, ch, d, c.topology.DeadLetterQueue(), decision.Retries, err, log)
	}
}

// forward publica uma cópia da mensagem em outra fila e só então confirma a original. Se a publicação falhar,
// devolve a original para a fila: pior caso, ela é processada de novo, e o handler é idempotente.
func (c *Consumer) forward(ctx context.Context, ch *amqp.Channel, d amqp.Delivery, queue string, retries int, cause error, log *slog.Logger) {
	if err := republish(ctx, ch, d, queue, retries, cause); err != nil {
		log.Error("falha ao republicar; devolvendo para a fila", "destino", queue, "erro", err)
		if nackErr := d.Nack(false, true); nackErr != nil {
			log.Error("falha no nack", "erro", nackErr)
		}
		return
	}
	ack(log, d)
}

func republish(ctx context.Context, ch *amqp.Channel, d amqp.Delivery, queue string, retries int, cause error) error {
	// Mesmo no desligamento, termina a republicação em andamento para não perder a mensagem.
	publishCtx, cancel := context.WithTimeout(context.WithoutCancel(ctx), 5*time.Second)
	defer cancel()

	headers := amqp.Table{}
	for k, v := range d.Headers {
		headers[k] = v
	}
	headers[retryCountHeader] = int32(retries)
	headers[lastErrorHeader] = truncate(cause.Error(), maxErrorLength)

	confirmation, err := ch.PublishWithDeferredConfirmWithContext(publishCtx, "", queue, false, false, amqp.Publishing{
		Headers:      headers,
		ContentType:  d.ContentType,
		MessageId:    d.MessageId,
		Type:         d.Type,
		Timestamp:    d.Timestamp,
		DeliveryMode: amqp.Persistent,
		Body:         d.Body,
	})
	if err != nil {
		return err
	}

	confirmed, err := confirmation.WaitContext(publishCtx)
	if err != nil {
		return err
	}
	if !confirmed {
		return errors.New("o broker recusou a mensagem (nack)")
	}
	return nil
}

func ack(log *slog.Logger, d amqp.Delivery) {
	if err := d.Ack(false); err != nil {
		log.Error("falha no ack", "erro", err)
	}
}

func retryCount(headers amqp.Table) int {
	switch v := headers[retryCountHeader].(type) {
	case int32:
		return int(v)
	case int64:
		return int(v)
	case int:
		return v
	}
	return 0
}

func truncate(s string, max int) string {
	if len(s) <= max {
		return s
	}
	return s[:max]
}
