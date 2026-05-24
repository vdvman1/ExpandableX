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
        private readonly Dictionary<string, Layout> _registrations = new Dictionary<string, Layout>();
        private readonly ConcurrentQueue<Action> _deferredActions = new ConcurrentQueue<Action>();

        public IReadOnlyDictionary<string, Layout> Registrations => _registrations;

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

        public RegistrationResult Register(Layout layout)
        {
            string groupId = layout.GroupId;
            if (_registrations.ContainsKey(groupId))
            {
                _logger.Info.Log($"ExpandableX-Core: {Describe(layout)} already registered, yielding (this mod's expandability for it is no longer needed)");
                return RegistrationResult.Yielded;
            }
            _registrations[groupId] = layout;
            _logger.Info.Log($"ExpandableX-Core: registered {Describe(layout)}");
            return RegistrationResult.Registered;
        }

        public RegistrationResult RegisterOverride(Layout layout)
        {
            string groupId = layout.GroupId;
            bool wasPresent = _registrations.ContainsKey(groupId);
            _registrations[groupId] = layout;
            _logger.Info.Log($"ExpandableX-Core: override-registered {Describe(layout)} ({(wasPresent ? "replacing prior registration" : "no prior registration")})");
            return wasPresent ? RegistrationResult.Overridden : RegistrationResult.Registered;
        }

        private static string Describe(Layout layout)
        {
            switch (layout)
            {
                case StaticLayout s:
                    return $"{s.GroupId} [StaticLayout, {s.Slots.Count} slot(s)]";
                case DynamicLayout d:
                    return $"{d.GroupId} [DynamicLayout]";
                default:
                    return $"{layout.GroupId} [{layout.GetType().Name}]";
            }
        }
    }

    public enum RegistrationResult
    {
        Registered,
        Yielded,
        Overridden,
    }
}
