using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using PayFlow.Ledger.Application.Telemetry;

namespace PayFlow.Ledger.UnitTests.Application;

/// <summary>
/// Cria um <see cref="LedgerMetrics"/> com meters próprios e soma o que for medido neles. Escuta só os meters
/// deste recorder, então testes rodando em paralelo não interferem uns nos outros.
/// </summary>
internal sealed class MetricsRecorder : IDisposable
{
    private readonly MeterFactory _factory = new();
    private readonly MeterListener _listener = new();
    private readonly ConcurrentDictionary<string, long> _totals = new();

    public MetricsRecorder()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (_factory.Owns(instrument.Meter))
                listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
            _totals.AddOrUpdate(instrument.Name, value, (_, total) => total + value));
        _listener.Start();

        Metrics = new LedgerMetrics(_factory);
    }

    public LedgerMetrics Metrics { get; }

    public long Total(string instrumentName) => _totals.GetValueOrDefault(instrumentName);

    public void Dispose()
    {
        _listener.Dispose();
        _factory.Dispose();
    }

    private sealed class MeterFactory : IMeterFactory
    {
        private readonly ConcurrentBag<Meter> _meters = [];

        public Meter Create(MeterOptions options)
        {
            var meter = new Meter(options);
            _meters.Add(meter);
            return meter;
        }

        public bool Owns(Meter meter) => _meters.Contains(meter);

        public void Dispose()
        {
            foreach (var meter in _meters)
                meter.Dispose();
        }
    }
}
