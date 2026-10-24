package messaging

import amqp "github.com/rabbitmq/amqp091-go"

// headerCarrier adapta os headers AMQP para a propagação de contexto do OpenTelemetry (traceparent).
type headerCarrier amqp.Table

func (c headerCarrier) Get(key string) string {
	switch v := c[key].(type) {
	case string:
		return v
	case []byte:
		return string(v)
	}
	return ""
}

func (c headerCarrier) Set(key, value string) {
	c[key] = value
}

func (c headerCarrier) Keys() []string {
	keys := make([]string, 0, len(c))
	for k := range c {
		keys = append(keys, k)
	}
	return keys
}
