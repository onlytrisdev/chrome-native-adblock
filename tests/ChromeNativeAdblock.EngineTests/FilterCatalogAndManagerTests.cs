using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ChromeNativeAdblock.Launcher;
using Xunit;

namespace ChromeNativeAdblock.EngineTests;

public sealed class FilterCatalogAndManagerTests
{
    [Fact]
    public void CatalogContainsAll4CategoriesAndRequiredSubGroups()
    {
        // 4 Categories matching uBlock Origin + EasyList core
        Assert.Equal(4, FilterCatalog.Categories.Count);
        Assert.True(FilterCatalog.Categories.Any(c => c.Id == FilterCatalog.CategoryBuiltin));
        Assert.True(FilterCatalog.Categories.Any(c => c.Id == FilterCatalog.CategoryAds));
        Assert.True(FilterCatalog.Categories.Any(c => c.Id == FilterCatalog.CategoryPrivacy));
        Assert.True(FilterCatalog.Categories.Any(c => c.Id == FilterCatalog.CategoryRegions));

        // Sub-groups
        Assert.Equal(1, FilterCatalog.SubGroups.Count);
        Assert.True(FilterCatalog.SubGroupsById.ContainsKey("subgroup-ublock-filters"));
    }

    [Fact]
    public void CatalogContainsAll8FilterItemsWithValidMetadata()
    {
        // Total 8 items across 4 categories
        Assert.Equal(8, FilterCatalog.Items.Count);

        // Built-in category: 5 items (all in subgroup)
        var builtinItems = FilterCatalog.GetItemsForCategory(FilterCatalog.CategoryBuiltin);
        Assert.Equal(5, builtinItems.Count);
        var ublockGroupItems = FilterCatalog.GetItemsForSubGroup("subgroup-ublock-filters");
        Assert.Equal(5, ublockGroupItems.Count);

        // Ads category: 1 item (EasyList)
        Assert.Equal(1, FilterCatalog.GetItemsForCategory(FilterCatalog.CategoryAds).Count);

        // Privacy category: 1 item (EasyPrivacy)
        Assert.Equal(1, FilterCatalog.GetItemsForCategory(FilterCatalog.CategoryPrivacy).Count);

        // Regions category: 1 item (ABPVN)
        Assert.Equal(1, FilterCatalog.GetItemsForCategory(FilterCatalog.CategoryRegions).Count);

        // Check every item has URL, FallbackFileName, and valid metadata
        foreach (var item in FilterCatalog.Items)
        {
            Assert.False(string.IsNullOrWhiteSpace(item.Url), $"Empty URL for filter: {item.Id}");
            Assert.False(string.IsNullOrWhiteSpace(item.FallbackFileName), $"Empty fallback for filter: {item.Id}");
            Assert.True(FilterCatalog.CategoriesById.ContainsKey(item.CategoryId), $"Invalid CategoryId: {item.CategoryId}");
            if (!string.IsNullOrEmpty(item.SubGroupId))
            {
                Assert.True(FilterCatalog.SubGroupsById.ContainsKey(item.SubGroupId), $"Invalid SubGroupId: {item.SubGroupId}");
            }
        }
    }

    [Fact]
    public void DefaultEnabledFiltersMatchSpecification()
    {
        var defaultEnabled = FilterCatalog.DefaultEnabledFilterIds;
        // 8 default enabled filters:
        // 5 uBlock built-ins + EasyList + EasyPrivacy + ABPVN (reg-vn)
        Assert.Equal(8, defaultEnabled.Count);

        // Built-in (5)
        Assert.True(defaultEnabled.Contains("ublock-filters"));
        Assert.True(defaultEnabled.Contains("ublock-badware"));
        Assert.True(defaultEnabled.Contains("ublock-privacy"));
        Assert.True(defaultEnabled.Contains("ublock-quick-fixes"));
        Assert.True(defaultEnabled.Contains("ublock-unbreak"));

        // Ads (1)
        Assert.True(defaultEnabled.Contains("easylist"));

        // Privacy (1)
        Assert.True(defaultEnabled.Contains("easyprivacy"));

        // Regions (1)
        Assert.True(defaultEnabled.Contains("reg-vn"));
    }

    [Fact]
    public async Task FilterManagerLoadsDefaultsAndCalculatesAccurateCounters()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "CnaTest_" + Guid.NewGuid().ToString("N"));
        var filtersDir = Path.Combine(tempDir, "filters");
        var settingsFile = Path.Combine(tempDir, "filter_settings.json");

        try
        {
            Directory.CreateDirectory(filtersDir);
            var manager = new FilterManager(filtersDir, settingsFile);

            // Default enabled checks
            Assert.True(manager.IsFilterEnabled("ublock-filters"));
            Assert.True(manager.IsFilterEnabled("easylist"));
            Assert.True(manager.IsFilterEnabled("reg-vn"));
            Assert.False(manager.IsFilterEnabled("reg-cn"));

            // SubGroup counters
            var ublockGroupCounts = manager.GetSubGroupCounts("subgroup-ublock-filters");
            Assert.Equal(5, ublockGroupCounts.EnabledCount);
            Assert.Equal(5, ublockGroupCounts.TotalCount);
            Assert.Equal(true, manager.GetSubGroupState("subgroup-ublock-filters")); // 5/5 -> Checked

            // Category counters
            var builtinCatCounts = manager.GetCategoryCounts(FilterCatalog.CategoryBuiltin);
            Assert.Equal(5, builtinCatCounts.EnabledCount);
            Assert.Equal(5, builtinCatCounts.TotalCount);
            Assert.Equal(true, manager.GetCategoryState(FilterCatalog.CategoryBuiltin));

            var adsCatCounts = manager.GetCategoryCounts(FilterCatalog.CategoryAds);
            Assert.Equal(1, adsCatCounts.EnabledCount);
            Assert.Equal(1, adsCatCounts.TotalCount);
            Assert.Equal(true, manager.GetCategoryState(FilterCatalog.CategoryAds));

            var privacyCatCounts = manager.GetCategoryCounts(FilterCatalog.CategoryPrivacy);
            Assert.Equal(1, privacyCatCounts.EnabledCount);
            Assert.Equal(1, privacyCatCounts.TotalCount);
            Assert.Equal(true, manager.GetCategoryState(FilterCatalog.CategoryPrivacy));

            var regionsCatCounts = manager.GetCategoryCounts(FilterCatalog.CategoryRegions);
            Assert.Equal(1, regionsCatCounts.EnabledCount);
            Assert.Equal(1, regionsCatCounts.TotalCount);
            Assert.Equal(true, manager.GetCategoryState(FilterCatalog.CategoryRegions));

            // Overall counts without cached files (0 rules until downloaded)
            var (totalEnabled, totalCount, totalRules) = manager.GetOverallCounts();
            Assert.Equal(8, totalEnabled);
            Assert.Equal(8, totalCount);
            Assert.Equal(0, totalRules);

            // Individual rule counts before download return null
            Assert.Null(manager.GetRuleCount("ublock-filters"));
            Assert.Null(manager.GetRuleCount("easylist"));
            Assert.Null(manager.GetRuleCount("reg-vn"));
            Assert.False(manager.IsFilterDownloaded("ublock-filters"));
            Assert.False(manager.IsFilterDownloaded("easylist"));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    [Fact]
    public async Task FilterManagerSubGroupAndCategoryTogglesWork()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "CnaTest_" + Guid.NewGuid().ToString("N"));
        var filtersDir = Path.Combine(tempDir, "filters");
        var settingsFile = Path.Combine(tempDir, "filter_settings.json");

        try
        {
            Directory.CreateDirectory(filtersDir);
            var manager = new FilterManager(filtersDir, settingsFile);

            // 1. Toggle entire Builtin category OFF
            await manager.SetCategoryFiltersAsync(FilterCatalog.CategoryBuiltin, false);
            var (cEnabled, cTotal) = manager.GetCategoryCounts(FilterCatalog.CategoryBuiltin);
            Assert.Equal(0, cEnabled);
            Assert.Equal(5, cTotal);
            Assert.Equal(false, manager.GetCategoryState(FilterCatalog.CategoryBuiltin));

            // Sub-groups under builtin are also all OFF
            Assert.Equal(false, manager.GetSubGroupState("subgroup-ublock-filters"));

            // 2. Toggle one filter back ON
            await manager.ToggleFilterAsync("ublock-filters", true);
            Assert.Equal(true, manager.IsFilterEnabled("ublock-filters"));
            Assert.Null(manager.GetCategoryState(FilterCatalog.CategoryBuiltin)); // 1/5 -> Mixed -> Indeterminate (null)

            // 3. Persist and reload
            var manager2 = new FilterManager(filtersDir, settingsFile);
            Assert.True(manager2.IsFilterEnabled("ublock-filters"));
            Assert.False(manager2.IsFilterEnabled("ublock-badware"));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    [Fact]
    public void FilterManagerRestoresExplicitlyEmptyCustomSelection()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "CnaEmptyCustom_" + Guid.NewGuid().ToString("N"));
        var filtersDir = Path.Combine(tempDir, "filters");
        var settingsFile = Path.Combine(tempDir, "filter_settings.json");

        try
        {
            Directory.CreateDirectory(filtersDir);
            File.WriteAllText(settingsFile, """
            {
              "EnabledFilters": [],
              "CurrentPreset": 4
            }
            """);

            var manager = new FilterManager(filtersDir, settingsFile);

            Assert.Empty(manager.EnabledFilterIds);
            Assert.Equal(BlockingPreset.Custom, manager.GetCurrentPreset());
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    [Fact]
    public async Task FilterManagerMergesOnlyEnabledFilters()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "CnaTest_" + Guid.NewGuid().ToString("N"));
        var filtersDir = Path.Combine(tempDir, "filters");
        var settingsFile = Path.Combine(tempDir, "filter_settings.json");

        try
        {
            Directory.CreateDirectory(filtersDir);
            var cacheDir = Path.Combine(filtersDir, "cache");
            Directory.CreateDirectory(cacheDir);

            await File.WriteAllTextAsync(Path.Combine(cacheDir, "easylist.txt"), "! EasyList\n||ads.google.com^\n||doubleclick.net^\n", Encoding.UTF8);
            await File.WriteAllTextAsync(Path.Combine(cacheDir, "ublock-filters.txt"), "! uBlock\n||ublock-test-ad.com^\n", Encoding.UTF8);
            await File.WriteAllTextAsync(Path.Combine(cacheDir, "reg-vn.txt"), "! Vietnam\n||vietnam-ad.com^\n", Encoding.UTF8);

            var manager = new FilterManager(filtersDir, settingsFile);
            await manager.ResetToDefaultsAsync();

            var ruleCount = await manager.MergeFiltersAsync();
            var combinedPath = manager.GetActiveCombinedFilterPath();
            Assert.True(File.Exists(combinedPath));

            var combinedContent = await File.ReadAllTextAsync(combinedPath, Encoding.UTF8);
            Assert.True(combinedContent.Contains("ads.google.com", StringComparison.Ordinal));
            Assert.True(combinedContent.Contains("ublock-test-ad.com", StringComparison.Ordinal));
            Assert.True(combinedContent.Contains("vietnam-ad.com", StringComparison.Ordinal));

            // Disable reg-vn filter and re-merge
            await manager.ToggleFilterAsync("reg-vn", false);
            var updatedContent = await File.ReadAllTextAsync(combinedPath, Encoding.UTF8);
            Assert.False(updatedContent.Contains("vietnam-ad.com", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    [Fact]
    public void FilterManagerCountRulesAccurate()
    {
        var sampleLines = new[]
        {
            "! Title: Sample Filter",
            "! Homepage: https://example.com",
            "[Adblock Plus 2.0]",
            "",
            "   ",
            "||adservice.com^",
            "||analytics.google.com^$third-party",
            "! Another comment",
            "##.sidebar-ad",
            "example.com#@##top-banner"
        };

        var count = FilterManager.CountRules(sampleLines);
        Assert.Equal(4, count);
    }

    [Fact]
    public async Task FilterManagerReturnsAccurateRuleCountsFromDiskAndCache()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "CnaTest_" + Guid.NewGuid().ToString("N"));
        var filtersDir = Path.Combine(tempDir, "filters");
        var settingsFile = Path.Combine(tempDir, "filter_settings.json");

        try
        {
            Directory.CreateDirectory(filtersDir);
            var manager = new FilterManager(filtersDir, settingsFile);

            // 1. Without cache or fallback files, GetRuleCount returns null and IsDownloaded returns false
            foreach (var item in FilterCatalog.Items)
            {
                Assert.Null(manager.GetRuleCount(item.Id));
                Assert.False(manager.IsFilterDownloaded(item.Id));
            }

            // 2. Fallback file on disk provides real line-counted rules
            var fallbackPath = Path.Combine(filtersDir, "abpvn_basic.txt");
            await File.WriteAllTextAsync(fallbackPath, "! Fallback\n||custom-vn-ad.com^\n||another-vn-ad.net^\n");

            var manager2 = new FilterManager(filtersDir, settingsFile);
            Assert.Equal(2, manager2.GetRuleCount("reg-vn"));
            Assert.True(manager2.IsFilterDownloaded("reg-vn"));

            // 3. Cache file overrides fallback file with exact counted lines
            var cacheDir = Path.Combine(filtersDir, "cache");
            Directory.CreateDirectory(cacheDir);
            var cachePath = Path.Combine(cacheDir, "reg-vn.txt");
            await File.WriteAllTextAsync(cachePath, "! Cached List\n||cached-vn-1.com^\n||cached-vn-2.com^\n||cached-vn-3.com^\n");

            var manager3 = new FilterManager(filtersDir, settingsFile);
            Assert.Equal(3, manager3.GetRuleCount("reg-vn"));
            Assert.True(manager3.IsFilterDownloaded("reg-vn"));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    [Fact]
    public async Task FilterManagerDownloadsRealFilterAndCalculatesExactRuleCount()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "CnaTest_" + Guid.NewGuid().ToString("N"));
        var filtersDir = Path.Combine(tempDir, "filters");
        var settingsFile = Path.Combine(tempDir, "filter_settings.json");

        var fakeHandler = new FakeHttpHandler(req =>
        {
            var content = "! Sample Live Downloaded Filter\n! Version: 2026.08.31\n[Adblock Plus 2.0]\n||ads.example.com^\n||tracker.example.com^\n@@||allowed.example.com^\n##.banner-ad\n###popup-ad\n";
            return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new System.Net.Http.StringContent(content, Encoding.UTF8, "text/plain")
            };
        });

        using var httpClient = new System.Net.Http.HttpClient(fakeHandler);

        try
        {
            Directory.CreateDirectory(filtersDir);
            var manager = new FilterManager(filtersDir, settingsFile, httpClient);
            await manager.ResetToDefaultsAsync();

            // Download enabled filters
            await manager.EnsureFiltersReadyAsync(forceUpdate: true);

            // Check that real files are downloaded and exact line counts parsed
            Assert.True(manager.IsFilterDownloaded("easylist"));
            Assert.Equal(5, manager.GetRuleCount("easylist")); // 5 rules out of 8 lines (3 comments/headers)

            var (totalEnabled, totalCount, totalRules) = manager.GetOverallCounts();
            Assert.Equal(8, totalEnabled);
            Assert.True(totalRules > 0);
            Assert.Equal(40, totalRules); // 8 enabled * 5 rules each
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    [Fact]
    public async Task FilterManagerRejectsOversizedResponseAndPreservesLastKnownGoodCache()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "CnaFilterLimitTest_" + Guid.NewGuid().ToString("N"));
        var filtersDir = Path.Combine(tempDir, "filters");
        var settingsFile = Path.Combine(tempDir, "filter_settings.json");
        var knownGood = "! known good\n||known-good.example^\n";

        var fakeHandler = new FakeHttpHandler(_ =>
        {
            var content = new System.Net.Http.StringContent("||replacement.example^\n", Encoding.UTF8, "text/plain");
            content.Headers.ContentLength = 64L * 1024 * 1024 + 1;
            return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content };
        });
        using var httpClient = new System.Net.Http.HttpClient(fakeHandler);

        try
        {
            var cacheDir = Path.Combine(filtersDir, "cache");
            Directory.CreateDirectory(cacheDir);
            var easyListCache = Path.Combine(cacheDir, "easylist.txt");
            await File.WriteAllTextAsync(easyListCache, knownGood);

            var manager = new FilterManager(filtersDir, settingsFile, httpClient);
            manager.ApplyPresetFast(BlockingPreset.Basic);
            await manager.EnsureFiltersReadyAsync(forceUpdate: true);

            Assert.Equal(knownGood, await File.ReadAllTextAsync(easyListCache));
            Assert.Equal(1, manager.GetRuleCount("easylist"));
            Assert.Empty(Directory.GetFiles(cacheDir, "*.download-*"));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    [Fact]
    public void PresetDefinitionsContainExactExpectedFilterIds()
    {
        // 1. Basic (2 filters)
        Assert.Equal(2, FilterCatalog.BasicPresetFilterIds.Count);
        Assert.True(FilterCatalog.BasicPresetFilterIds.Contains("easylist"));
        Assert.True(FilterCatalog.BasicPresetFilterIds.Contains("reg-vn"));

        // 2. Standard (8 filters - matches defaults)
        Assert.Equal(8, FilterCatalog.StandardPresetFilterIds.Count);
        Assert.True(FilterCatalog.StandardPresetFilterIds.SetEquals(FilterCatalog.DefaultEnabledFilterIds));

        // 3. Advanced (8 filters)
        Assert.Equal(8, FilterCatalog.AdvancedPresetFilterIds.Count);

        // 4. Max (8 filters)
        Assert.Equal(8, FilterCatalog.MaxPresetFilterIds.Count);

        // All preset filters exist in the catalog
        foreach (var id in FilterCatalog.MaxPresetFilterIds)
        {
            Assert.True(FilterCatalog.ItemsById.ContainsKey(id), $"Filter item in preset not in catalog: {id}");
        }
    }

    [Fact]
    public async Task FilterManagerAppliesAndDetectsAllPresetsAccurately()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "CnaPresetTest_" + Guid.NewGuid().ToString("N"));
        var filtersDir = Path.Combine(tempDir, "filters");
        var settingsFile = Path.Combine(tempDir, "filter_settings.json");

        var fakeHandler = new FakeHttpHandler(req =>
        {
            var content = "||example.com^\n";
            return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new System.Net.Http.StringContent(content, Encoding.UTF8, "text/plain")
            };
        });

        using var httpClient = new System.Net.Http.HttpClient(fakeHandler);

        try
        {
            Directory.CreateDirectory(filtersDir);
            var manager = new FilterManager(filtersDir, settingsFile, httpClient);

            // Default should be Standard preset
            Assert.Equal(BlockingPreset.Standard, manager.GetCurrentPreset());
            Assert.Equal(8, manager.EnabledFilterIds.Count);

            // 1. Switch to Basic
            manager.ApplyPresetFast(BlockingPreset.Basic);
            Assert.Equal(BlockingPreset.Basic, manager.GetCurrentPreset());
            Assert.Equal(2, manager.EnabledFilterIds.Count);
            Assert.True(manager.IsFilterEnabled("easylist"));
            Assert.True(manager.IsFilterEnabled("reg-vn"));
            Assert.False(manager.IsFilterEnabled("ublock-filters"));

            // 2. Custom detection when user manually toggles an individual filter
            manager.SetFilterEnabled("ublock-filters", true);
            Assert.Equal(BlockingPreset.Custom, manager.GetCurrentPreset());
            Assert.Equal(3, manager.EnabledFilterIds.Count);

            // 3. Test Async application & merge
            await manager.ApplyPresetAsync(BlockingPreset.Standard);
            Assert.Equal(BlockingPreset.Standard, manager.GetCurrentPreset());
            Assert.Equal(8, manager.EnabledFilterIds.Count);
            Assert.True(File.Exists(manager.CombinedRulesPath));

            // 4. Test Settings Persistence & Reload
            var reloadedManager = new FilterManager(filtersDir, settingsFile, httpClient);
            Assert.Equal(BlockingPreset.Standard, reloadedManager.GetCurrentPreset());
            Assert.Equal(8, reloadedManager.EnabledFilterIds.Count);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    private sealed class FakeHttpHandler : System.Net.Http.HttpMessageHandler
    {
        private readonly Func<System.Net.Http.HttpRequestMessage, System.Net.Http.HttpResponseMessage> _handler;
        public FakeHttpHandler(Func<System.Net.Http.HttpRequestMessage, System.Net.Http.HttpResponseMessage> handler) => _handler = handler;
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, System.Threading.CancellationToken cancellationToken) =>
            Task.FromResult(_handler(request));
    }
}
