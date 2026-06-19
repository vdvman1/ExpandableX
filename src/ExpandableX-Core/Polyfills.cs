// netstandard2.1 has no System.Runtime.CompilerServices.IsExternalInit, which the C# compiler needs for
// record `init` accessors. It is normally PolySharp's job to polyfill this — but PolySharp only generates
// a polyfill when the type is absent from referenced assemblies, and MonoMod.RuntimeDetour (referenced for
// Hook) bundles its own IsExternalInit. PolySharp detects MonoMod's copy by metadata name and stands down.
// We `extern alias` MonoMod out of the global namespace to dodge a NotNullWhen clash with netstandard (see
// ExpandableX-Core.csproj); that hides MonoMod's IsExternalInit from the compiler's `init` lookup but NOT
// from PolySharp's metadata check, so PolySharp still won't generate it. Hence we declare it here.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit
    {
    }
}
