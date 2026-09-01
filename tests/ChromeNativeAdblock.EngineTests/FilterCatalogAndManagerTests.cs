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
    public void CatalogContainsAll9CategoriesAndRequiredSubGroups()
    {
        // 9 Categories matching uBlock Origin
        Assert.Equal(9, FilterCatalog.Categories.Count);
        Assert.True(FilterCatalog.Categories.Any(c => c.Id == FilterCatalog.CategoryBuiltin));
        Assert.True(FilterCatalog.Categories.Any(c => c.Id == FilterCatalog.CategoryAds));
        Assert.True(FilterCatalog.Categories.Any(c => c.Id == FilterCatalog.CategoryPrivacy));
        Assert.True(FilterCatalog.Categories.Any(c => c.Id == FilterCatalog.CategorySecurity));
        Assert.True(FilterCatalog.Categories.Any(c => c.Id == FilterCatalog.CategoryMultipurpose));
        Assert.True(FilterCatalog.Categories.Any(c => c.Id == FilterCatalog.CategoryCookies));
        Assert.True(FilterCatalog.Categories.Any(c => c.Id == FilterCatalog.CategorySocial));
        Assert.True(FilterCatalog.Categories.Any(c => c.Id == FilterCatalog.CategoryAnnoyances));
        Assert.True(FilterCatalog.Categories.Any(c => c.Id == FilterCatalog.CategoryRegions));

        // Sub-groups
        Assert.Equal(7, FilterCatalog.SubGroups.Count);
        Assert.True(FilterCatalog.SubGroupsById.ContainsKey("subgroup-ublock-filters"));
        Assert.True(FilterCatalog.SubGroupsById.ContainsKey("subgroup-easylist-cookies"));
        Assert.True(FilterCatalog.SubGroupsById.ContainsKey("subgroup-adguard-cookies"));
        Assert.True(FilterCatalog.SubGroupsById.ContainsKey("subgroup-easylist-annoyances"));
        Assert.True(FilterCatalog.SubGroupsById.ContainsKey("subgroup-adguard-annoyances"));
        Assert.True(FilterCatalog.SubGroupsById.ContainsKey("subgroup-pl"));
        Assert.True(FilterCatalog.SubGroupsById.ContainsKey("subgroup-ru"));
    }

    [Fact]
    public void CatalogContainsAll72FilterItemsWithValidMetadata()
    {
        // Total 72 items across all 9 categories
        Assert.Equal(72, FilterCatalog.Items.Count);

        // 38 Regional items in CategoryRegions
        var regionalItems = FilterCatalog.GetItemsForCategory(FilterCatalog.CategoryRegions);
        Assert.Equal(38, regionalItems.Count);

        // Built-in category: 6 items (5 in subgroup, 1 standalone)
        var builtinItems = FilterCatalog.GetItemsForCategory(FilterCatalog.CategoryBuiltin);
        Assert.Equal(6, builtinItems.Count);
        var ublockGroupItems = FilterCatalog.GetItemsForSubGroup("subgroup-ublock-filters");
        Assert.Equal(5, ublockGroupItems.Count);

        // Ads category: 4 items
        Assert.Equal(4, FilterCatalog.GetItemsForCategory(FilterCatalog.CategoryAds).Count);

        // Privacy category: 3 items
        Assert.Equal(3, FilterCatalog.GetItemsForCategory(FilterCatalog.CategoryPrivacy).Count);

        // Security category: 2 items
        Assert.Equal(2, FilterCatalog.GetItemsForCategory(FilterCatalog.CategorySecurity).Count);

        // Multipurpose category: 2 items
        Assert.Equal(2, FilterCatalog.GetItemsForCategory(FilterCatalog.CategoryMultipurpose).Count);

        // Cookies category: 4 items (2 in EasyList subgroup, 2 in AdGuard subgroup)
        Assert.Equal(4, FilterCatalog.GetItemsForCategory(FilterCatalog.CategoryCookies).Count);
        Assert.Equal(2, FilterCatalog.GetItemsForSubGroup("subgroup-easylist-cookies").Count);
        Assert.Equal(2, FilterCatalog.GetItemsForSubGroup("subgroup-adguard-cookies").Count);

        // Social category: 3 items
        Assert.Equal(3, FilterCatalog.GetItemsForCategory(FilterCatalog.CategorySocial).Count);

        // Annoyances category: 10 items (5 EasyList, 4 AdGuard, 1 uBO)
        Assert.Equal(10, FilterCatalog.GetItemsForCategory(FilterCatalog.CategoryAnnoyances).Count);
        Assert.Equal(5, FilterCatalog.GetItemsForSubGroup("subgroup-easylist-annoyances").Count);
        Assert.Equal(4, FilterCatalog.GetItemsForSubGroup("subgroup-adguard-annoyances").Count);

        // Polish & RU sub-groups in Regions
        Assert.Equal(2, FilterCatalog.GetItemsForSubGroup("subgroup-pl").Count);
        Assert.Equal(2, FilterCatalog.GetItemsForSubGroup("subgroup-ru").Count);

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
        // 9 default enabled filters:
        // 5 uBlock built-ins + EasyList + YouTube + EasyPrivacy + ABPVN (reg-vn)
        Assert.Equal(9, defaultEnabled.Count);

        // Built-in (5)
        Assert.True(defaultEnabled.Contains("ublock-filters"));
        Assert.True(defaultEnabled.Contains("ublock-badware"));
        Assert.True(defaultEnabled.Contains("ublock-privacy"));
        Assert.True(defaultEnabled.Contains("ublock-quick-fixes"));
        Assert.True(defaultEnabled.Contains("ublock-unbreak"));

        // Ads (2)
        Assert.True(defaultEnabled.Contains("easylist"));
        Assert.True(defaultEnabled.Contains("youtube-adblock"));

        // Privacy (1)
        Assert.True(defaultEnabled.Contains("easyprivacy"));

        // Regions (1)
        Assert.True(defaultEnabled.Contains("reg-vn"));

        // Others must be disabled by default
        Assert.False(defaultEnabled.Contains("ublock-experimental"));
        Assert.False(defaultEnabled.Contains("adguard-base"));
        Assert.False(defaultEnabled.Contains("malicious-urls"));
        Assert.False(defaultEnabled.Contains("reg-cn"));
        Assert.False(defaultEnabled.Contains("reg-de"));
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

            var cookiesGroupCounts = manager.GetSubGroupCounts("subgroup-easylist-cookies");
            Assert.Equal(0, cookiesGroupCounts.EnabledCount);
            Assert.Equal(2, cookiesGroupCounts.TotalCount);
            Assert.Equal(false, manager.GetSubGroupState("subgroup-easylist-cookies")); // 0/2 -> Unchecked

            // Category counters
            var builtinCatCounts = manager.GetCategoryCounts(FilterCatalog.CategoryBuiltin);
            Assert.Equal(5, builtinCatCounts.EnabledCount);
            Assert.Equal(6, builtinCatCounts.TotalCount);
            Assert.Null(manager.GetCategoryState(FilterCatalog.CategoryBuiltin)); // 5/6 -> Indeterminate (null)

            var adsCatCounts = manager.GetCategoryCounts(FilterCatalog.CategoryAds);
            Assert.Equal(2, adsCatCounts.EnabledCount);
            Assert.Equal(4, adsCatCounts.TotalCount);
            Assert.Null(manager.GetCategoryState(FilterCatalog.CategoryAds)); // 2/4 -> Indeterminate (null)

            var securityCatCounts = manager.GetCategoryCounts(FilterCatalog.CategorySecurity);
            Assert.Equal(0, securityCatCounts.EnabledCount);
            Assert.Equal(2, securityCatCounts.TotalCount);
            Assert.Equal(false, manager.GetCategoryState(FilterCatalog.CategorySecurity)); // 0/2 -> Unchecked

            var regionsCatCounts = manager.GetCategoryCounts(FilterCatalog.CategoryRegions);
            Assert.Equal(1, regionsCatCounts.EnabledCount);
            Assert.Equal(38, regionsCatCounts.TotalCount); // 1/38
            Assert.Null(manager.GetCategoryState(FilterCatalog.CategoryRegions));

            // Overall counts without cached files (0 rules until downloaded)
            var (totalEnabled, totalCount, totalRules) = manager.GetOverallCounts();
            Assert.Equal(9, totalEnabled);
            Assert.Equal(72, totalCount);
            Assert.Equal(0, totalRules);

            // Individual rule counts before download return null
            Assert.Null(manager.GetRuleCount("ublock-filters"));
            Assert.Null(manager.GetRuleCount("easylist"));
            Assert.Null(manager.GetRuleCount("reg-vn"));
            Assert.Null(manager.GetRuleCount("adguard-base"));
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

            // 1. Toggle entire Cookies category ON
            await manager.SetCategoryFiltersAsync(FilterCatalog.CategoryCookies, true);
            var (cEnabled, cTotal) = manager.GetCategoryCounts(FilterCatalog.CategoryCookies);
            Assert.Equal(4, cEnabled);
            Assert.Equal(4, cTotal);
            Assert.Equal(true, manager.GetCategoryState(FilterCatalog.CategoryCookies));

            // Sub-groups under cookies are also all ON
            Assert.Equal(true, manager.GetSubGroupState("subgroup-easylist-cookies"));
            Assert.Equal(true, manager.GetSubGroupState("subgroup-adguard-cookies"));

            // 2. Toggle one sub-group OFF
            await manager.SetSubGroupFiltersAsync("subgroup-easylist-cookies", false);
            Assert.Equal(false, manager.GetSubGroupState("subgroup-easylist-cookies"));
            Assert.Equal(true, manager.GetSubGroupState("subgroup-adguard-cookies"));
            Assert.Null(manager.GetCategoryState(FilterCatalog.CategoryCookies)); // Mixed -> Indeterminate

            // 3. Persist and reload
            var manager2 = new FilterManager(filtersDir, settingsFile);
            Assert.False(manager2.IsFilterEnabled("easylist-cookies"));
            Assert.True(manager2.IsFilterEnabled("adguard-cookies"));
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
            await File.WriteAllTextAsync(Path.Combine(cacheDir, "youtube-adblock.txt"), "! YouTube\n||googlevideo.com/videoplayback?*adformat^\n", Encoding.UTF8);
            await File.WriteAllTextAsync(Path.Combine(cacheDir, "reg-cn.txt"), "! Chinese\n||baidu-ad.com^\n", Encoding.UTF8);

            var manager = new FilterManager(filtersDir, settingsFile);
            await manager.ResetToDefaultsAsync();

            var ruleCount = await manager.MergeFiltersAsync();
            var combinedPath = manager.GetActiveCombinedFilterPath();
            Assert.True(File.Exists(combinedPath));

            var combinedContent = await File.ReadAllTextAsync(combinedPath, Encoding.UTF8);
            Assert.True(combinedContent.Contains("ads.google.com", StringComparison.Ordinal));
            Assert.True(combinedContent.Contains("googlevideo.com", StringComparison.Ordinal));
            Assert.False(combinedContent.Contains("baidu-ad.com", StringComparison.Ordinal));

            // Enable Chinese filter and re-merge
            await manager.ToggleFilterAsync("reg-cn", true);
            var updatedContent = await File.ReadAllTextAsync(combinedPath, Encoding.UTF8);
            Assert.True(updatedContent.Contains("baidu-ad.com", StringComparison.Ordinal));
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
            Assert.Equal(9, totalEnabled);
            Assert.True(totalRules > 0);
            Assert.Equal(45, totalRules); // 9 enabled * 5 rules each
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
        // 1. Basic (3 filters)
        Assert.Equal(3, FilterCatalog.BasicPresetFilterIds.Count);
        Assert.True(FilterCatalog.BasicPresetFilterIds.Contains("easylist"));
        Assert.True(FilterCatalog.BasicPresetFilterIds.Contains("reg-vn"));
        Assert.True(FilterCatalog.BasicPresetFilterIds.Contains("youtube-adblock"));

        // 2. Standard (9 filters - matches defaults)
        Assert.Equal(9, FilterCatalog.StandardPresetFilterIds.Count);
        Assert.True(FilterCatalog.StandardPresetFilterIds.SetEquals(FilterCatalog.DefaultEnabledFilterIds));

        // 3. Advanced (18 filters)
        Assert.Equal(18, FilterCatalog.AdvancedPresetFilterIds.Count);
        foreach (var id in FilterCatalog.StandardPresetFilterIds)
        {
            Assert.True(FilterCatalog.AdvancedPresetFilterIds.Contains(id), $"Advanced missing standard filter: {id}");
        }
        Assert.True(FilterCatalog.AdvancedPresetFilterIds.Contains("adguard-base"));
        Assert.True(FilterCatalog.AdvancedPresetFilterIds.Contains("malicious-urls"));
        Assert.True(FilterCatalog.AdvancedPresetFilterIds.Contains("phishing-urls"));
        Assert.True(FilterCatalog.AdvancedPresetFilterIds.Contains("easylist-cookies"));
        Assert.True(FilterCatalog.AdvancedPresetFilterIds.Contains("easylist-other-annoyances"));
        Assert.True(FilterCatalog.AdvancedPresetFilterIds.Contains("adguard-mobile-app-banners"));
        Assert.True(FilterCatalog.AdvancedPresetFilterIds.Contains("adguard-other-annoyances"));
        Assert.True(FilterCatalog.AdvancedPresetFilterIds.Contains("adguard-popup-overlays"));
        Assert.True(FilterCatalog.AdvancedPresetFilterIds.Contains("adguard-widgets"));

        // 4. Max (33 filters)
        Assert.Equal(33, FilterCatalog.MaxPresetFilterIds.Count);
        foreach (var id in FilterCatalog.AdvancedPresetFilterIds)
        {
            Assert.True(FilterCatalog.MaxPresetFilterIds.Contains(id), $"Max missing advanced filter: {id}");
        }
        Assert.True(FilterCatalog.MaxPresetFilterIds.Contains("adguard-tracking"));
        Assert.True(FilterCatalog.MaxPresetFilterIds.Contains("adguard-mobile"));
        Assert.True(FilterCatalog.MaxPresetFilterIds.Contains("block-lan"));
        Assert.True(FilterCatalog.MaxPresetFilterIds.Contains("dan-pollock"));
        Assert.True(FilterCatalog.MaxPresetFilterIds.Contains("peter-lowe"));
        Assert.True(FilterCatalog.MaxPresetFilterIds.Contains("easylist-social"));
        Assert.True(FilterCatalog.MaxPresetFilterIds.Contains("fanboy-antifacebook"));
        Assert.True(FilterCatalog.MaxPresetFilterIds.Contains("ublock-cookies-easylist"));
        Assert.True(FilterCatalog.MaxPresetFilterIds.Contains("adguard-cookies"));
        Assert.True(FilterCatalog.MaxPresetFilterIds.Contains("ublock-cookies-adguard"));
        Assert.True(FilterCatalog.MaxPresetFilterIds.Contains("easylist-ai"));
        Assert.True(FilterCatalog.MaxPresetFilterIds.Contains("easylist-chat"));
        Assert.True(FilterCatalog.MaxPresetFilterIds.Contains("easylist-newsletters"));
        Assert.True(FilterCatalog.MaxPresetFilterIds.Contains("easylist-notifications"));
        Assert.True(FilterCatalog.MaxPresetFilterIds.Contains("ublock-annoyances"));

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
            Assert.Equal(9, manager.EnabledFilterIds.Count);

            // 1. Switch to Basic
            manager.ApplyPresetFast(BlockingPreset.Basic);
            Assert.Equal(BlockingPreset.Basic, manager.GetCurrentPreset());
            Assert.Equal(3, manager.EnabledFilterIds.Count);
            Assert.True(manager.IsFilterEnabled("easylist"));
            Assert.True(manager.IsFilterEnabled("reg-vn"));
            Assert.True(manager.IsFilterEnabled("youtube-adblock"));
            Assert.False(manager.IsFilterEnabled("ublock-filters"));

            // 2. Switch to Advanced
            manager.ApplyPresetFast(BlockingPreset.Advanced);
            Assert.Equal(BlockingPreset.Advanced, manager.GetCurrentPreset());
            Assert.Equal(18, manager.EnabledFilterIds.Count);
            Assert.True(manager.IsFilterEnabled("adguard-base"));
            Assert.True(manager.IsFilterEnabled("malicious-urls"));
            Assert.True(manager.IsFilterEnabled("easylist-cookies"));
            Assert.False(manager.IsFilterEnabled("adguard-tracking"));

            // 3. Switch to Max
            manager.ApplyPresetFast(BlockingPreset.Max);
            Assert.Equal(BlockingPreset.Max, manager.GetCurrentPreset());
            Assert.Equal(33, manager.EnabledFilterIds.Count);
            Assert.True(manager.IsFilterEnabled("adguard-tracking"));
            Assert.True(manager.IsFilterEnabled("dan-pollock"));
            Assert.True(manager.IsFilterEnabled("ublock-annoyances"));

            // 4. Custom detection when user manually toggles an individual filter
            manager.SetFilterEnabled("reg-cn", true);
            Assert.Equal(BlockingPreset.Custom, manager.GetCurrentPreset());
            Assert.Equal(34, manager.EnabledFilterIds.Count);

            manager.SetFilterEnabled("reg-cn", false);
            Assert.Equal(BlockingPreset.Max, manager.GetCurrentPreset());

            // 5. Test Async application & merge
            await manager.ApplyPresetAsync(BlockingPreset.Standard);
            Assert.Equal(BlockingPreset.Standard, manager.GetCurrentPreset());
            Assert.Equal(9, manager.EnabledFilterIds.Count);
            Assert.True(File.Exists(manager.CombinedRulesPath));

            // 6. Test Settings Persistence & Reload
            var reloadedManager = new FilterManager(filtersDir, settingsFile, httpClient);
            Assert.Equal(BlockingPreset.Standard, reloadedManager.GetCurrentPreset());
            Assert.Equal(9, reloadedManager.EnabledFilterIds.Count);
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
