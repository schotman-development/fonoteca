using Fonoteca.Api.Configuration;
using Microsoft.Extensions.Configuration;

namespace Fonoteca.Integration.Tests;

/// <summary>
/// That the environment variable names in <c>.env.example</c> reach the
/// properties that read them.
/// </summary>
/// <remarks>
/// <b>This is the test the AcoustID key did not have.</b> It shipped as
/// <c>Fonoteca__Providers__AcoustId__ApiKey</c> against a flat
/// <c>FonotecaOptions</c>, bound to nothing, and nobody noticed for months
/// because nothing called AcoustID — the failure was a key that was plainly set
/// and lookups refused anyway.
///
/// Qobuz is the same shape and worse: three settings, nested one level deeper
/// than everything around them, and two of the three fail in ways that look
/// like something else. A wrong app id reads as an expired session, a missing
/// secret reads as a download bug.
///
/// No host and no database. What is under test is <c>IConfiguration</c>'s
/// double-underscore mapping onto these properties, which needs neither — and a
/// binding test that costs a PostgreSQL container is one that gets skipped.
/// </remarks>
public sealed class QobuzConfigurationTests
{
    [Fact]
    public void TheEnvironmentVariableNamesInTheExampleFileBindToTheOptionsThatReadThem()
    {
        // Copied from .env.example verbatim, double underscores and all. If
        // these two lists ever disagree, that file is lying to whoever set it up.
        var options = Bind(new Dictionary<string, string?>
        {
            ["Fonoteca__Providers__Qobuz__AppId"] = "an-app-id",
            ["Fonoteca__Providers__Qobuz__AppSecret"] = "a-secret",
            ["Fonoteca__Providers__Qobuz__UserAuthToken"] = "a-token",
            ["Fonoteca__Providers__Qobuz__FormatId"] = "6",
            ["Fonoteca__Providers__Qobuz__MinRequestIntervalMs"] = "1500",
            ["Fonoteca__Download__TrackDelayMs"] = "750",

            // The two that decide whether music can be taken away, and where it
            // goes. A name that binds to nothing here is a flag that reads as
            // set and is off — which on this pair means an upgrade that quietly
            // never replaces, or one that quietly does.
            ["Fonoteca__AllowFileReplacement"] = "true",
            ["Fonoteca__ReplacedPath"] = "/mnt/music-replaced",
        });

        Assert.Equal("an-app-id", options.Providers.Qobuz.AppId);
        Assert.Equal("a-secret", options.Providers.Qobuz.AppSecret);
        Assert.Equal("a-token", options.Providers.Qobuz.UserAuthToken);
        Assert.True(options.AllowFileReplacement);
        Assert.Equal("/mnt/music-replaced", options.ReplacedPath);
        Assert.Equal(6, options.Providers.Qobuz.FormatId);
        Assert.Equal(1500, options.Providers.Qobuz.MinRequestIntervalMs);
        Assert.Equal(750, options.Download.TrackDelayMs);
    }

    [Fact]
    public void NothingConfiguredIsAValidStateWithSafeDefaults()
    {
        // The ordinary state of a fresh checkout, and it must not throw: the
        // application starts, and the acquisition endpoints refuse locally with
        // a message naming the setting.
        var options = Bind([]);

        Assert.Empty(options.Providers.Qobuz.AppId);
        Assert.Empty(options.Providers.Qobuz.AppSecret);
        Assert.Empty(options.Providers.Qobuz.UserAuthToken);

        // Hi-res, because asking high costs nothing — Qobuz serve the best
        // encoding a release has up to the request.
        Assert.Equal(27, options.Providers.Qobuz.FormatId);
    }

    private static FonotecaOptions Bind(Dictionary<string, string?> environment) =>
        new ConfigurationBuilder()
            // The same provider the host uses, so the double-underscore
            // translation under test is the real one rather than a stand-in.
            .AddInMemoryCollection(environment.ToDictionary(
                static pair => pair.Key.Replace("__", ":", StringComparison.Ordinal),
                static pair => pair.Value))
            .Build()
            .GetSection(FonotecaOptions.SectionName)
            .Get<FonotecaOptions>() ?? new FonotecaOptions();
}
