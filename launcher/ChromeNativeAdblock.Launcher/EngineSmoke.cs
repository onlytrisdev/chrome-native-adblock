using System.Text.Json.Serialization;

namespace ChromeNativeAdblock.Launcher;

internal sealed record EngineSmokeResult(
    bool Success,
    string Version,
    bool BlockedExpectedRequest,
    bool AllowedExceptionRequest,
    bool CosmeticExtractionSuccess,
    int ExtractedSelectorsCount,
    bool SerializationRoundtripSuccess,
    string? Error);

internal static class EngineSmoke
{
    internal static EngineSmokeResult Run(string dllPath, string filterPath)
    {
        try
        {
            using var engine = new NativeEngine(dllPath);
            var version = engine.GetVersion();

            // 1. Load initial filter file
            engine.LoadFilterFile(filterPath);

            // 2. Load rich test rules with cosmetic rules and scriptlets
            var richRules = """
||ads.example^$script
@@||ads.example/allowed.js$script
*/blocked.png
example.com##.ad-banner
youtube.com##.ytd-ad-slot-renderer
youtube.com##.ytp-ad-module
youtube.com##+js(set-constant, ytInitialPlayerResponse.adPlacements, undefined)
""";
            engine.LoadFilterText(richRules);

            // 3. Test network check
            var blockedScript = engine.CheckRequest(
                "https://ads.example/banner.js",
                "https://site.example/",
                "script",
                "GET");
            var allowedScript = engine.CheckRequest(
                "https://ads.example/allowed.js",
                "https://site.example/",
                "script",
                "GET");
            var blockedGeneric = engine.CheckRequest(
                "http://127.0.0.1:12345/blocked.png",
                "http://127.0.0.1:12345/",
                "image",
                "GET");

            // 4. Test cosmetic resource extraction
            var cosmeticYt = engine.GetUrlCosmeticResources("https://www.youtube.com/watch?v=smokeTest");
            var cosmeticExample = engine.GetUrlCosmeticResources("https://example.com/");

            var cosmeticOk = cosmeticYt.HideSelectors.Contains(".ytd-ad-slot-renderer") &&
                             cosmeticYt.InjectedScript.Contains("ytInitialPlayerResponse.adPlacements") &&
                             cosmeticExample.HideSelectors.Contains(".ad-banner");

            var totalSelectors = cosmeticYt.HideSelectors.Length + cosmeticExample.HideSelectors.Length;

            // 5. Test serialization & deserialization roundtrip
            var serialized = engine.Serialize();
            var serializationOk = serialized.Length > 0;

            engine.Deserialize(serialized);

            // Verify post-deserialization rules
            var blockedAfterDeser = engine.CheckRequest("https://ads.example/banner.js", "https://site.example/", "script", "GET");
            var allowedAfterDeser = engine.CheckRequest("https://ads.example/allowed.js", "https://site.example/", "script", "GET");
            var cosmeticAfterDeser = engine.GetUrlCosmeticResources("https://example.com/");
            var postDeserOk = blockedAfterDeser && !allowedAfterDeser && cosmeticAfterDeser.HideSelectors.Contains(".ad-banner");

            // 6. Test hook query C ABI
            var initialMode = engine.GetHookResolutionMode();
            var hookQueryOk = initialMode == HookResolutionMode.NotInstalled;

            var overallSuccess = blockedScript && !allowedScript && blockedGeneric &&
                                 cosmeticOk && serializationOk && postDeserOk && hookQueryOk;
            return new EngineSmokeResult(
                overallSuccess,
                version,
                blockedScript,
                !allowedScript,
                cosmeticOk,
                totalSelectors,
                serializationOk && postDeserOk,
                null);
        }
        catch (Exception ex)
        {
            return new EngineSmokeResult(
                false,
                "unknown",
                false,
                false,
                false,
                0,
                false,
                ex.Message);
        }
    }
}
