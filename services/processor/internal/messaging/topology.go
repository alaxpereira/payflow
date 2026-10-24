// Package messaging cuida da conexão com o RabbitMQ, da topologia de filas e da política de retentativas.
package messaging

import (
	"fmt"
	"time"

	amqp "github.com/rabbitmq/amqp091-go"
)

// Exchanges compartilhadas com o ledger. Os argumentos precisam ser idênticos aos declarados em
// EventTopology.cs, senão o RabbitMQ recusa a declaração (PRECONDITION_FAILED).
const (
	Exchange               = "payflow.events"
	UnroutedExchange       = "payflow.events.unrouted"
	UnroutedQueue          = "payflow.events.unrouted"
	unroutedQueueMaxLength = int32(100_000) // int32, igual ao int do C#: o tipo também precisa bater.
)

// Topology descreve a fila principal do processor, as filas de retentativa e a DLQ.
//
// Existe uma fila de retentativa por atraso, e não uma única fila com TTL por mensagem: o RabbitMQ só expira
// mensagens do início da fila, então uma mensagem de 60s na frente seguraria outra de 1s logo atrás.
type Topology struct {
	Queue      string
	RoutingKey string
	Retry      RetryPolicy
}

func DefaultTopology(retry RetryPolicy) Topology {
	return Topology{
		Queue:      "processor.transactions",
		RoutingKey: "ledger.transaction_completed",
		Retry:      retry,
	}
}

func (t Topology) RetryQueue(delay time.Duration) string {
	return fmt.Sprintf("%s.retry.%dms", t.Queue, delay.Milliseconds())
}

func (t Topology) DeadLetterQueue() string {
	return t.Queue + ".dlq"
}

// Declare cria (ou confirma) toda a topologia. É idempotente.
func (t Topology) Declare(ch *amqp.Channel) error {
	quorum := amqp.Table{"x-queue-type": "quorum"}

	steps := []struct {
		what string
		run  func() error
	}{
		{"exchange " + UnroutedExchange, func() error {
			return ch.ExchangeDeclare(UnroutedExchange, amqp.ExchangeFanout, true, false, false, false, nil)
		}},
		{"fila " + UnroutedQueue, func() error {
			_, err := ch.QueueDeclare(UnroutedQueue, true, false, false, false,
				amqp.Table{"x-queue-type": "quorum", "x-max-length": unroutedQueueMaxLength})
			return err
		}},
		{"bind " + UnroutedQueue, func() error {
			return ch.QueueBind(UnroutedQueue, "", UnroutedExchange, false, nil)
		}},
		{"exchange " + Exchange, func() error {
			return ch.ExchangeDeclare(Exchange, amqp.ExchangeTopic, true, false, false, false,
				amqp.Table{"alternate-exchange": UnroutedExchange})
		}},
		{"fila " + t.Queue, func() error {
			_, err := ch.QueueDeclare(t.Queue, true, false, false, false, quorum)
			return err
		}},
		{"bind " + t.Queue, func() error {
			return ch.QueueBind(t.Queue, t.RoutingKey, Exchange, false, nil)
		}},
		{"fila " + t.DeadLetterQueue(), func() error {
			_, err := ch.QueueDeclare(t.DeadLetterQueue(), true, false, false, false, quorum)
			return err
		}},
	}

	for _, delay := range t.Retry.Delays {
		queue := t.RetryQueue(delay)
		ttl := delay.Milliseconds()
		steps = append(steps, struct {
			what string
			run  func() error
		}{"fila " + queue, func() error {
			// Ao expirar, a mensagem volta para a fila principal pela exchange padrão.
			_, err := ch.QueueDeclare(queue, true, false, false, false, amqp.Table{
				"x-queue-type":              "quorum",
				"x-message-ttl":             ttl,
				"x-dead-letter-exchange":    "",
				"x-dead-letter-routing-key": t.Queue,
			})
			return err
		}})
	}

	for _, step := range steps {
		if err := step.run(); err != nil {
			return fmt.Errorf("declarando %s: %w", step.what, err)
		}
	}
	return nil
}
