// Test-only stub: provides a no-op SubframesLogger so guide-sample
// files can be compiled into the test project without NINA.Core dependencies.

// ReSharper disable once CheckNamespace
namespace Subframes.NinaPlugin;

internal static class SubframesLogger
{
    public static void Info(string message) { }
    public static void Debug(string message) { }
    public static void Warning(string message) { }
    public static void Error(string message) { }
    public static void Trace(string message) { }
    public static void Initialize() { }
    public static void Shutdown() { }
}
