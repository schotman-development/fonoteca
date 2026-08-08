using System.Reflection;

namespace Fonoteca.Api.Startup;

/// <summary>
/// Tells apart a real run from a tool that is only borrowing the host.
/// </summary>
/// <remarks>
/// Several tools start the application in-process to inspect it:
///
///   - <c>GetDocument.Insider</c>, which the build-time OpenAPI generator runs
///     during an ordinary <c>dotnet build</c>
///   - <c>ef</c>, the Entity Framework command-line tool
///
/// Both start hosted services, so a hosted service alone is not enough to keep
/// startup work out of a build. Without this check, compiling the project would
/// try to connect to PostgreSQL and apply migrations — which fails on any
/// machine that does not happen to have the database running, and silently
/// migrates a real database on one that does.
///
/// Detecting by entry-assembly name is the established workaround; the tools
/// expose no first-class flag.
/// </remarks>
internal static class HostRuntime
{
    private static readonly string[] ToolEntryAssemblies = ["GetDocument.Insider", "ef"];

    public static bool IsDesignTime
    {
        get
        {
            var entry = Assembly.GetEntryAssembly()?.GetName().Name;
            return entry is not null && Array.Exists(
                ToolEntryAssemblies,
                name => string.Equals(name, entry, StringComparison.Ordinal));
        }
    }
}
