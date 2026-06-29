using System.Collections.Generic;
using ILogger = Core.Logging.ILogger;

namespace ExpandableX.Core
{
    public class ExpandableXRegistry
    {
        public static ExpandableXRegistry Instance { get; private set; }

        public GameMode CurrentMode { get; internal set; }

        /// <summary>The session's player action manager, captured once at session init so slot
        /// changes can be dispatched as undoable actions. Null until a session has started.</summary>
        public PlayerActionManager PlayerActions { get; internal set; }

        /// <summary>The local player, captured alongside <see cref="PlayerActions"/> (needed to author actions).</summary>
        public Player LocalPlayer { get; internal set; }

        /// <summary>
        /// The session's placed-building map, resolved from the DI container at session init so the
        /// drag-handle draw hook (issue #5) can compute expansion handles off-frame from the selection
        /// (the HUD module path receives the map as a parameter, but the world-space draw hook has no such
        /// parameter). Null until a session has started, or if the container doesn't expose it — the hook
        /// then simply draws no handles (fail-open).
        /// </summary>
        public IMapModel Map { get; internal set; }

        /// <summary>
        /// The session's viewport, resolved from the DI container at session init. The drag-handle input
        /// hook (issue #5) uses it to project the cursor to a world position for hit-testing, since it runs
        /// off the HUD dispatcher rather than the mass-selection HUD's own ComputeCursorWorldPosition. Null
        /// until a session has started; the input hook then does nothing (fail-open).
        /// </summary>
        public Viewport Viewport { get; internal set; }

        /// <summary>
        /// The session's shared network matcher, set when at least one <see cref="Layout.Dynamic"/> family
        /// supplies a simulation factory. It is the authoritative source of network membership (the
        /// connected components of join-adjacent pieces), which grow/shrink re-validation queries rather
        /// than re-deriving the graph. Null when no network-model family is simulated.
        /// </summary>
        internal ExpandableSimulationSystem? NetworkSimulation { get; set; }

        /// <summary>
        /// The current session's atomic-selection / focus-piece manager (ADR-0013), captured so the HUD
        /// panel-gating detours can read the focus piece. Re-created per session; null outside a session,
        /// in which case those detours are inert.
        /// </summary>
        internal ExpandableXNetworkSelection? NetworkSelection { get; set; }

        /// <summary>
        /// The session's <see cref="VisualTheme"/>, resolved from the game's dependency container at session
        /// init. Cached here because the per-frame <c>FrameDrawOptions.Theme</c> is obsolete ("Theme should
        /// be injected to drawers") — resolving it once via DI is the supported equivalent for our draw
        /// hooks. Null outside a session.
        /// </summary>
        internal VisualTheme? SessionTheme { get; set; }

        private readonly ILogger _logger;
        private readonly Dictionary<string, Registration> _registrations = new Dictionary<string, Registration>();
        private readonly Dictionary<string, VariantPlacement> _variantsByDefId = new Dictionary<string, VariantPlacement>();

        public IReadOnlyDictionary<string, Registration> Registrations => _registrations;

        /// <summary>
        /// Decode catalog: variant (or base / override) definition id name → what it represents.
        /// Populated per session by the simulation-systems rewirer; idempotent across re-runs since
        /// ids are deterministic. Consumed by the slot UI / swap logic.
        /// </summary>
        public IReadOnlyDictionary<string, VariantPlacement> VariantsByDefId => _variantsByDefId;

        internal void RecordVariant(string definitionIdName, VariantPlacement placement) =>
            _variantsByDefId[definitionIdName] = placement;

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
