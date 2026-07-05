using Game.Core.Rendering.MeshGeneration;
using ShapezShifter.Flow.Atomic;
using ShapezShifter.Hijack;
using ILogger = Core.Logging.ILogger;

namespace ExpandableX.Core
{
    /// <summary>
    /// Captures the session's <see cref="IMeshCache"/> and <see cref="VisualThemeBaseResources"/> during
    /// the buildings phase and stashes them on the registry. The later simulation-systems phase — where
    /// variants are synthesised — doesn't expose either, but needs them to bake composed models
    /// (ADR-0016, <see cref="VariantModelComposer"/>). This runs strictly before that phase (same
    /// ordering the consumer's configurable-base authoring relies on), so the references are ready in
    /// time. Returns <c>GameBuildings</c> unchanged — it adds nothing, it only observes.
    /// </summary>
    internal sealed class ExpandableXBuildingsRewirer : IBuildingsRewirer
    {
        private readonly ILogger _logger;
        private readonly ExpandableXRegistry _registry;

        public ExpandableXBuildingsRewirer(ILogger logger, ExpandableXRegistry registry)
        {
            _logger = logger;
            _registry = registry;
        }

        public GameBuildings ModifyGameBuildings(
            AuthoringBuildings meta,
            GameBuildings gameBuildings,
            IMeshCache meshCache,
            VisualThemeBaseResources theme)
        {
            _registry.MeshCache = meshCache;
            _registry.ThemeResources = theme;
            _logger.Info.Log("ExpandableX-Core: captured mesh cache + theme resources for composed variant models");
            return gameBuildings;
        }
    }
}
