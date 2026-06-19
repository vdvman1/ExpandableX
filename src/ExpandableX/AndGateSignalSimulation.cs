using Game.Content.Features.Signals;
using Game.Content.Features.Signals.Conductor;
using Game.Content.Features.Signals.Connections;
using Game.Content.Features.Signals.Simulation;
using Game.Content.Features.Signals.Tick;
using Game.Core.Simulation;

namespace ExpandableX
{
    /// <summary>
    /// The AND gate's gameplay simulation, generalised to N inputs and M outputs — the base game's
    /// <c>LogicGate2In1OutSimulation</c> is hardwired to 2 inputs / 1 output. The output is high iff
    /// every input is truthy on a given signal tick; all outputs carry the same result.
    ///
    /// This single class serves both a standalone AND building and a multi-piece AND network: the
    /// connectable node (<see cref="ExpandableX.Core.SignalExpandableSimulation{TSimulation}"/>) wires whatever perimeter signal
    /// connectors its member set exposes to this sim's receivers/providers, so the gate logic is written
    /// once regardless of member count (ADR-0012). It is stateless across rebuilds (combinational), so
    /// it holds only fresh conductor buffers — nothing persisted. The per-tick loop mirrors the base
    /// game's gate: pop each input for the tick, AND, push to the outputs.
    /// </summary>
    internal sealed class AndGateSignalSimulation : ISignalSimulation, IUpdatableSimulation
    {
        private readonly SignalConductorInput[] _inputs;
        private readonly SignalConductorOutput[] _outputs;

        public AndGateSignalSimulation(int inputCount, int outputCount)
        {
            _inputs = new SignalConductorInput[inputCount];
            for (int i = 0; i < inputCount; i++)
            {
                // Fresh transient buffer per construction — v1 networks are stateless (rebuild-on-change).
                _inputs[i] = new SignalConductorInput(new SignalConductorInputState());
            }

            _outputs = new SignalConductorOutput[outputCount];
            for (int i = 0; i < outputCount; i++)
            {
                _outputs[i] = new SignalConductorOutput();
            }
        }

        public int NumSignalReceivers => _inputs.Length;
        public int NumSignalProviders => _outputs.Length;

        public ISignalReceiver GetSignalReceiver(int index) => _inputs[index];
        public ISignalProvider GetSignalProvider(int index) => _outputs[index];

        public void Update(Ticks startTicks, Ticks deltaTicks)
        {
            int signalsThisUpdate = SignalSimulation.GetAmountOfSignalsThisUpdate(startTicks, deltaTicks);
            SignalTicks baseTick = SignalTicks.FromTicks(startTicks);
            for (int s = 0; s < signalsThisUpdate; s++)
            {
                var signalTick = new SignalTicks(baseTick.NumOfTicks + s);

                // Pop every input for this tick (draining each buffer) before deciding, then AND. A gate
                // with no inputs holds its output low rather than being vacuously high.
                bool all = _inputs.Length > 0;
                for (int i = 0; i < _inputs.Length; i++)
                {
                    _inputs[i].TryPopSignal(startTicks, signalTick, out ISignal value);
                    if (!value.IsTruthy())
                    {
                        all = false;
                    }
                }

                ISignal result = IntegerSignal.Get(all);
                for (int o = 0; o < _outputs.Length; o++)
                {
                    _outputs[o].PushSignal(result, startTicks, signalTick);
                }
            }
        }

        public void ClearContent()
        {
            for (int o = 0; o < _outputs.Length; o++)
            {
                _outputs[o].Last = NullSignal.Instance;
            }
        }
    }
}
