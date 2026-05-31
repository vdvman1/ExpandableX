using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using ILogger = Core.Logging.ILogger;

namespace ExpandableX.Core
{
    public class ExpandableXRegistry
    {
        public static ExpandableXRegistry Instance { get; private set; }

        public GameMode CurrentMode { get; internal set; }

        private readonly ILogger _logger;
        private readonly Dictionary<string, Registration> _registrations = new Dictionary<string, Registration>();
        private readonly Dictionary<string, VariantPlacement> _variantsByDefId = new Dictionary<string, VariantPlacement>();
        private readonly ConcurrentQueue<Action> _deferredActions = new ConcurrentQueue<Action>();

        public IReadOnlyDictionary<string, Registration> Registrations => _registrations;

        /// <summary>
        /// Decode catalog: variant (or base / override) definition id name → what it represents.
        /// Populated per session by the simulation-systems rewirer; idempotent across re-runs since
        /// ids are deterministic. Consumed by the slot UI / swap logic.
        /// </summary>
        public IReadOnlyDictionary<string, VariantPlacement> VariantsByDefId => _variantsByDefId;

        internal void RecordVariant(string definitionIdName, VariantPlacement placement) =>
            _variantsByDefId[definitionIdName] = placement;

        public void EnqueueDeferred(Action action)
        {
            _deferredActions.Enqueue(action);
        }

        internal void DrainDeferred()
        {
            while (_deferredActions.TryDequeue(out Action action))
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    _logger.Info.Log($"ExpandableX-Core: deferred action threw: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        internal static void Initialize(ILogger logger)
        {
            Instance = new ExpandableXRegistry(logger);
        }

        private ExpandableXRegistry(ILogger logger)
        {
            _logger = logger;
        }

        public RegistrationResult Register(Registration registration)
        {
            string id = registration.RegistrationId;
            if (_registrations.ContainsKey(id))
            {
                _logger.Info.Log($"ExpandableX-Core: {Describe(registration)} already registered, yielding (this mod's expandability for it is no longer needed)");
                return RegistrationResult.Yielded;
            }
            _registrations[id] = registration;
            _logger.Info.Log($"ExpandableX-Core: registered {Describe(registration)}");
            return RegistrationResult.Registered;
        }

        public RegistrationResult RegisterOverride(Registration registration)
        {
            string id = registration.RegistrationId;
            bool wasPresent = _registrations.ContainsKey(id);
            _registrations[id] = registration;
            _logger.Info.Log($"ExpandableX-Core: override-registered {Describe(registration)} ({(wasPresent ? "replacing prior registration" : "no prior registration")})");
            return wasPresent ? RegistrationResult.Overridden : RegistrationResult.Registered;
        }

        private static string Describe(Registration registration) =>
            $"{registration.RegistrationId} [{registration.Layouts.Count} layout(s), {registration.Expansions.Count} expansion(s)]";
    }

    public enum RegistrationResult
    {
        Registered,
        Yielded,
        Overridden,
    }
}
