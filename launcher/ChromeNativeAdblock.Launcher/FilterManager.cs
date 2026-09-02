using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ChromeNativeAdblock.Launcher;

public sealed class FilterSettingsModel
{
    public List<string> EnabledFilters { get; set; } = [];
    public DateTime? LastUpdatedUtc { get; set; }
    public BlockingPreset? CurrentPreset { get; set; }
}

public sealed class FilterItemState
{
    public required FilterItemDefinition Definition { get; init; }
    public bool IsEnabled { get; set; }
    public int? RuleCount { get; set; }
    public bool IsCached { get; set; }
    public bool IsDownloaded => IsCached && RuleCount.HasValue && RuleCount.Value > 0;
}

public sealed class FilterSubGroupState
{
    public required FilterSubGroupDefinition Definition { get; init; }
    public required List<FilterItemState> Items { get; init; }
    public int EnabledCount => Items.Count(i => i.IsEnabled);
    public int TotalCount => Items.Count;
    public bool? State => EnabledCount == 0 ? false : (EnabledCount == TotalCount ? true : null);
}

public sealed class FilterCategoryGroup
{
    public required FilterCategoryDefinition Category { get; init; }
    public required List<FilterItemState> StandaloneItems { get; init; }
    public required List<FilterSubGroupState> SubGroups { get; init; }
    public int EnabledCount => StandaloneItems.Count(i => i.IsEnabled) + SubGroups.Sum(sg => sg.EnabledCount);
    public int TotalCount => StandaloneItems.Count + SubGroups.Sum(sg => sg.TotalCount);
    public int TotalRules => StandaloneItems.Where(i => i.IsEnabled && i.RuleCount.HasValue).Sum(i => i.RuleCount!.Value) +
                             SubGroups.SelectMany(sg => sg.Items).Where(i => i.IsEnabled && i.RuleCount.HasValue).Sum(i => i.RuleCount!.Value);
    public bool? State => EnabledCount == 0 ? false : (EnabledCount == TotalCount ? true : null);
}

public class FilterManager
{
    private const int MaxFilterDownloadBytes = 64 * 1024 * 1024;
    private readonly string _filtersDir;
    private readonly string _cacheDir;
    private readonly string _combinedRulesPath;
    private readonly string _settingsPath;
    private readonly HttpClient _httpClient;
    private readonly HashSet<string> _enabledFilterIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _ruleCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _sync = new();
    private readonly SemaphoreSlim _mergeSemaphore = new(1, 1);
    public string FiltersDirectory => _filtersDir;
    public string CacheDirectory => _cacheDir;
    public string CombinedRulesPath => _combinedRulesPath;
    public string SettingsPath => _settingsPath;

    public IReadOnlySet<string> EnabledFilterIds
    {
        get
        {
            lock (_sync)
            {
                return new HashSet<string>(_enabledFilterIds, StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    public FilterManager(string? filtersDir = null, string? settingsPath = null, HttpClient? httpClient = null)
    {
        _filtersDir = ResolveFiltersDirectory(filtersDir);
        _cacheDir = Path.Combine(_filtersDir, "cache");
        _combinedRulesPath = Path.Combine(_filtersDir, "combined_rules.txt");
        _settingsPath = settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ChromeNativeAdblock",
            "filter_settings.json");

        _httpClient = httpClient ?? new HttpClient();
        LoadSettings();
        PreloadCachedRuleCounts();
    }

    public IReadOnlyList<FilterCategoryDefinition> GetCategories() => FilterCatalog.Categories;
    public IReadOnlyList<FilterSubGroupDefinition> GetSubGroups() => FilterCatalog.SubGroups;
    public IReadOnlyList<FilterItemDefinition> GetAllFilters() => FilterCatalog.Items;

    public bool IsFilterEnabled(string id)
    {
        lock (_sync)
        {
            var norm = NormalizeFilterId(id);
            return _enabledFilterIds.Contains(norm);
        }
    }

    public int? GetRuleCount(string filterId)
    {
        var norm = NormalizeFilterId(filterId);
        if (_ruleCounts.TryGetValue(norm, out var count))
        {
            return count;
        }

        var cacheFile = GetCacheFilePath(norm);
        if (File.Exists(cacheFile))
        {
            count = CountRulesInFile(cacheFile);
            _ruleCounts[norm] = count;
            return count;
        }

        if (FilterCatalog.ItemsById.TryGetValue(norm, out var def))
        {
            var fallback = Path.Combine(_filtersDir, def.FallbackFileName);
            if (File.Exists(fallback))
            {
                count = CountRulesInFile(fallback);
                _ruleCounts[norm] = count;
                return count;
            }
        }

        return null;
    }

    public bool IsFilterDownloaded(string filterId)
    {
        var norm = NormalizeFilterId(filterId);
        var cacheFile = GetCacheFilePath(norm);
        if (File.Exists(cacheFile))
        {
            var count = GetRuleCount(norm);
            return count.HasValue && count.Value > 0;
        }

        if (FilterCatalog.ItemsById.TryGetValue(norm, out var def))
        {
            var fallback = Path.Combine(_filtersDir, def.FallbackFileName);
            if (File.Exists(fallback))
            {
                var count = GetRuleCount(norm);
                return count.HasValue && count.Value > 0;
            }
        }

        return false;
    }

    public IReadOnlyList<FilterCategoryGroup> GetCatalog()
    {
        lock (_sync)
        {
            var groups = new List<FilterCategoryGroup>();
            foreach (var cat in FilterCatalog.Categories.OrderBy(c => c.DisplayOrder))
            {
                var standaloneItems = FilterCatalog.Items
                    .Where(i => string.Equals(i.CategoryId, cat.Id, StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(i.SubGroupId))
                    .OrderBy(i => i.DisplayOrder)
                    .Select(def => new FilterItemState
                    {
                        Definition = def,
                        IsEnabled = _enabledFilterIds.Contains(def.Id),
                        RuleCount = GetRuleCount(def.Id),
                        IsCached = File.Exists(GetCacheFilePath(def.Id))
                    })
                    .ToList();

                var subGroups = FilterCatalog.GetSubGroupsForCategory(cat.Id)
                    .Select(sg =>
                    {
                        var items = FilterCatalog.GetItemsForSubGroup(sg.Id)
                            .Select(def => new FilterItemState
                            {
                                Definition = def,
                                IsEnabled = _enabledFilterIds.Contains(def.Id),
                                RuleCount = GetRuleCount(def.Id),
                                IsCached = File.Exists(GetCacheFilePath(def.Id))
                            })
                            .ToList();

                        return new FilterSubGroupState
                        {
                            Definition = sg,
                            Items = items
                        };
                    })
                    .ToList();

                groups.Add(new FilterCategoryGroup
                {
                    Category = cat,
                    StandaloneItems = standaloneItems,
                    SubGroups = subGroups
                });
            }
            return groups;
        }
    }

    public (int EnabledCount, int TotalCount) GetCategoryCounts(string categoryId)
    {
        lock (_sync)
        {
            var items = FilterCatalog.Items
                .Where(i => string.Equals(i.CategoryId, categoryId, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var total = items.Count;
            var enabled = items.Count(i => _enabledFilterIds.Contains(i.Id));
            return (enabled, total);
        }
    }

    public (int EnabledCount, int TotalCount) GetSubGroupCounts(string subGroupId)
    {
        lock (_sync)
        {
            var items = FilterCatalog.Items
                .Where(i => string.Equals(i.SubGroupId, subGroupId, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var total = items.Count;
            var enabled = items.Count(i => _enabledFilterIds.Contains(i.Id));
            return (enabled, total);
        }
    }

    public bool? GetCategoryState(string categoryId)
    {
        var (enabled, total) = GetCategoryCounts(categoryId);
        if (total == 0 || enabled == 0) return false;
        if (enabled == total) return true;
        return null;
    }

    public bool? GetSubGroupState(string subGroupId)
    {
        var (enabled, total) = GetSubGroupCounts(subGroupId);
        if (total == 0 || enabled == 0) return false;
        if (enabled == total) return true;
        return null;
    }

    public (int TotalEnabled, int TotalCount, int TotalCombinedRules) GetOverallCounts()
    {
        lock (_sync)
        {
            var totalCount = FilterCatalog.Items.Count;
            var enabledCount = _enabledFilterIds.Count;
            var totalRules = 0;
            if (File.Exists(_combinedRulesPath))
            {
                totalRules = CountRulesInFile(_combinedRulesPath);
            }
            else
            {
                foreach (var id in _enabledFilterIds)
                {
                    var count = GetRuleCount(id);
                    if (count.HasValue)
                    {
                        totalRules += count.Value;
                    }
                }
            }
            return (enabledCount, totalCount, totalRules);
        }
    }

    public void SetFilterEnabled(string id, bool enabled)
    {
        var norm = NormalizeFilterId(id);
        lock (_sync)
        {
            if (enabled)
            {
                _enabledFilterIds.Add(norm);
            }
            else
            {
                _enabledFilterIds.Remove(norm);
            }
            SaveSettings();
        }
    }

    public async Task<bool> ToggleFilterAsync(string id, bool enabled)
    {
        var norm = NormalizeFilterId(id);
        Task? downloadTask = null;
        lock (_sync)
        {
            if (enabled)
            {
                _enabledFilterIds.Add(norm);
                var cacheFile = GetCacheFilePath(norm);
                if (!File.Exists(cacheFile) && FilterCatalog.ItemsById.TryGetValue(norm, out var def))
                {
                    downloadTask = DownloadFilterAsync(def, cacheFile, null);
                }
            }
            else
            {
                _enabledFilterIds.Remove(norm);
            }
            SaveSettings();
        }

        if (downloadTask != null)
        {
            await downloadTask;
        }

        await MergeFiltersAsync();
        return true;
    }

    public void ToggleFilter(string id, bool enabled)
    {
        ToggleFilterAsync(id, enabled).GetAwaiter().GetResult();
    }

    public void SetCategoryFiltersFast(string categoryId, bool enabled)
    {
        lock (_sync)
        {
            var items = FilterCatalog.Items
                .Where(i => string.Equals(i.CategoryId, categoryId, StringComparison.OrdinalIgnoreCase));

            foreach (var item in items)
            {
                if (enabled)
                {
                    _enabledFilterIds.Add(item.Id);
                }
                else
                {
                    _enabledFilterIds.Remove(item.Id);
                }
            }
            SaveSettings();
        }
    }

    public async Task SetCategoryFiltersAsync(string categoryId, bool enabled)
    {
        var downloadTasks = new List<Task>();
        lock (_sync)
        {
            var items = FilterCatalog.Items
                .Where(i => string.Equals(i.CategoryId, categoryId, StringComparison.OrdinalIgnoreCase));

            foreach (var item in items)
            {
                if (enabled)
                {
                    _enabledFilterIds.Add(item.Id);
                    var cacheFile = GetCacheFilePath(item.Id);
                    if (!File.Exists(cacheFile))
                    {
                        downloadTasks.Add(DownloadFilterAsync(item, cacheFile, null));
                    }
                }
                else
                {
                    _enabledFilterIds.Remove(item.Id);
                }
            }
            SaveSettings();
        }

        if (downloadTasks.Count > 0)
        {
            await Task.WhenAll(downloadTasks);
        }

        await MergeFiltersAsync();
    }

    public void SetCategoryFilters(string categoryId, bool enabled)
    {
        SetCategoryFiltersAsync(categoryId, enabled).GetAwaiter().GetResult();
    }

    public void SetSubGroupFiltersFast(string subGroupId, bool enabled)
    {
        lock (_sync)
        {
            var items = FilterCatalog.Items
                .Where(i => string.Equals(i.SubGroupId, subGroupId, StringComparison.OrdinalIgnoreCase));

            foreach (var item in items)
            {
                if (enabled)
                {
                    _enabledFilterIds.Add(item.Id);
                }
                else
                {
                    _enabledFilterIds.Remove(item.Id);
                }
            }
            SaveSettings();
        }
    }

    public async Task SetSubGroupFiltersAsync(string subGroupId, bool enabled)
    {
        var downloadTasks = new List<Task>();
        lock (_sync)
        {
            var items = FilterCatalog.Items
                .Where(i => string.Equals(i.SubGroupId, subGroupId, StringComparison.OrdinalIgnoreCase));

            foreach (var item in items)
            {
                if (enabled)
                {
                    _enabledFilterIds.Add(item.Id);
                    var cacheFile = GetCacheFilePath(item.Id);
                    if (!File.Exists(cacheFile))
                    {
                        downloadTasks.Add(DownloadFilterAsync(item, cacheFile, null));
                    }
                }
                else
                {
                    _enabledFilterIds.Remove(item.Id);
                }
            }
            SaveSettings();
        }

        if (downloadTasks.Count > 0)
        {
            await Task.WhenAll(downloadTasks);
        }

        await MergeFiltersAsync();
    }

    public void SetSubGroupFilters(string subGroupId, bool enabled)
    {
        SetSubGroupFiltersAsync(subGroupId, enabled).GetAwaiter().GetResult();
    }

    public BlockingPreset GetCurrentPreset()
    {
        lock (_sync)
        {
            if (_enabledFilterIds.SetEquals(FilterCatalog.BasicPresetFilterIds))
                return BlockingPreset.Basic;
            if (_enabledFilterIds.SetEquals(FilterCatalog.StandardPresetFilterIds))
                return BlockingPreset.Standard;
            if (_enabledFilterIds.SetEquals(FilterCatalog.AdvancedPresetFilterIds))
                return BlockingPreset.Advanced;
            if (_enabledFilterIds.SetEquals(FilterCatalog.MaxPresetFilterIds))
                return BlockingPreset.Max;
            return BlockingPreset.Custom;
        }
    }

    public void ApplyPresetFast(BlockingPreset preset)
    {
        if (preset == BlockingPreset.Custom) return;

        var targetIds = FilterCatalog.GetFilterIdsForPreset(preset);
        lock (_sync)
        {
            _enabledFilterIds.Clear();
            foreach (var id in targetIds)
            {
                _enabledFilterIds.Add(id);
            }
            SaveSettings();
        }
    }

    public async Task ApplyPresetAsync(BlockingPreset preset)
    {
        if (preset == BlockingPreset.Custom) return;

        var targetIds = FilterCatalog.GetFilterIdsForPreset(preset);
        var downloadTasks = new List<Task>();
        lock (_sync)
        {
            _enabledFilterIds.Clear();
            foreach (var id in targetIds)
            {
                _enabledFilterIds.Add(id);
                var cacheFile = GetCacheFilePath(id);
                if (!File.Exists(cacheFile) && FilterCatalog.ItemsById.TryGetValue(id, out var def))
                {
                    downloadTasks.Add(DownloadFilterAsync(def, cacheFile, null));
                }
            }
            SaveSettings();
        }

        if (downloadTasks.Count > 0)
        {
            await Task.WhenAll(downloadTasks);
        }

        await MergeFiltersAsync();
    }

    public void ApplyPreset(BlockingPreset preset)
    {
        ApplyPresetAsync(preset).GetAwaiter().GetResult();
    }

    public async Task ResetToDefaultsAsync()
    {
        var downloadTasks = new List<Task>();
        lock (_sync)
        {
            _enabledFilterIds.Clear();
            foreach (var id in FilterCatalog.DefaultEnabledFilterIds)
            {
                _enabledFilterIds.Add(id);
                var cacheFile = GetCacheFilePath(id);
                if (!File.Exists(cacheFile) && FilterCatalog.ItemsById.TryGetValue(id, out var def))
                {
                    downloadTasks.Add(DownloadFilterAsync(def, cacheFile, null));
                }
            }
            SaveSettings();
        }

        if (downloadTasks.Count > 0)
        {
            await Task.WhenAll(downloadTasks);
        }

        await MergeFiltersAsync();
    }


    public async Task EnsureFiltersReadyAsync(bool forceUpdate = false, Action<string>? logCallback = null)
    {
        Directory.CreateDirectory(_filtersDir);
        Directory.CreateDirectory(_cacheDir);

        var tasks = new List<Task>();
        List<FilterItemDefinition> activeItems;
        lock (_sync)
        {
            activeItems = FilterCatalog.Items
                .Where(i => _enabledFilterIds.Contains(i.Id))
                .ToList();
        }

        foreach (var item in activeItems)
        {
            var cacheFile = GetCacheFilePath(item.Id);
            MigrateLegacyCacheFileIfNeeded(item.Id, cacheFile);

            var needsDownload = forceUpdate ||
                                !File.Exists(cacheFile) ||
                                (DateTime.UtcNow - File.GetLastWriteTimeUtc(cacheFile)).TotalHours > 24;

            if (needsDownload)
            {
                tasks.Add(DownloadFilterAsync(item, cacheFile, logCallback));
            }
            else
            {
                _ruleCounts[item.Id] = CountRulesInFile(cacheFile);
            }
        }

        if (tasks.Count > 0)
        {
            await Task.WhenAll(tasks);
        }

        await MergeFiltersAsync();
    }

    private async Task DownloadFilterAsync(FilterItemDefinition item, string cacheFile, Action<string>? logCallback)
    {
        string? temporaryFile = null;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var req = new HttpRequestMessage(HttpMethod.Get, item.Url);
            req.Headers.TryAddWithoutValidation("User-Agent", "ChromeNativeAdblock/1.0");

            using var response = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaxFilterDownloadBytes)
            {
                throw new InvalidDataException($"Filter response exceeds the {MaxFilterDownloadBytes / 1024 / 1024} MiB limit.");
            }

            await using var responseStream = await response.Content.ReadAsStreamAsync(cts.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[64 * 1024];
            while (true)
            {
                var read = await responseStream.ReadAsync(chunk, cts.Token);
                if (read == 0) break;
                if (buffer.Length + read > MaxFilterDownloadBytes)
                {
                    throw new InvalidDataException($"Filter response exceeds the {MaxFilterDownloadBytes / 1024 / 1024} MiB limit.");
                }
                await buffer.WriteAsync(chunk.AsMemory(0, read), cts.Token);
            }

            var content = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
            var ruleCount = CountRules(content.Split('\n'));
            if (string.IsNullOrWhiteSpace(content) || ruleCount == 0)
            {
                throw new InvalidDataException("Downloaded content does not contain any supported filter rules.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(cacheFile) ?? _cacheDir);
            temporaryFile = cacheFile + ".download-" + Guid.NewGuid().ToString("N");
            await File.WriteAllTextAsync(temporaryFile, content, new UTF8Encoding(false), cts.Token);
            File.Move(temporaryFile, cacheFile, true);
            temporaryFile = null;
            _ruleCounts[item.Id] = ruleCount;
            logCallback?.Invoke($"[FilterManager] Updated '{item.DefaultName}' ({ruleCount:N0} rules).");
            return;
        }
        catch (Exception ex)
        {
            logCallback?.Invoke($"[FilterManager] Download failed for '{item.DefaultName}': {ex.Message}. Checking fallbacks.");
        }
        finally
        {
            if (temporaryFile != null)
            {
                try { File.Delete(temporaryFile); } catch { }
            }
        }

        // Fallback resolution
        if (!File.Exists(cacheFile))
        {
            var fallback = Path.Combine(_filtersDir, item.FallbackFileName);
            if (File.Exists(fallback))
            {
                File.Copy(fallback, cacheFile, true);
                var ruleCount = CountRulesInFile(cacheFile);
                _ruleCounts[item.Id] = ruleCount;
                logCallback?.Invoke($"[FilterManager] Applied bundled fallback for '{item.DefaultName}' ({ruleCount:N0} rules).");
            }
        }
        else
        {
            _ruleCounts[item.Id] = CountRulesInFile(cacheFile);
        }
    }

    public async Task<int> MergeFiltersAsync()
    {
        await _mergeSemaphore.WaitAsync();
        try
        {
            Directory.CreateDirectory(_filtersDir);
            Directory.CreateDirectory(_cacheDir);

            var lines = new List<string>();

            lock (_sync)
            {
                lines.Add($"! Combined Rules - Generated by ChromeNativeAdblock at {DateTime.UtcNow:O}");
                lines.Add($"! Total Enabled Subscriptions: {_enabledFilterIds.Count}");
                lines.Add(string.Empty);

                foreach (var item in FilterCatalog.Items.OrderBy(i => i.DisplayOrder))
                {
                    var cacheFile = GetCacheFilePath(item.Id);
                    MigrateLegacyCacheFileIfNeeded(item.Id, cacheFile);

                    if (_enabledFilterIds.Contains(item.Id))
                    {
                        if (File.Exists(cacheFile))
                        {
                            var fileLines = File.ReadAllLines(cacheFile);
                            lines.Add($"! ========================================================");
                            lines.Add($"! Subscription: {item.DefaultName} [{item.Id}]");
                            lines.Add($"! ========================================================");
                            lines.AddRange(fileLines);
                            lines.Add(string.Empty);
                            _ruleCounts[item.Id] = CountRules(fileLines);
                        }
                        else
                        {
                            var fallback = Path.Combine(_filtersDir, item.FallbackFileName);
                            if (File.Exists(fallback))
                            {
                                File.Copy(fallback, cacheFile, true);
                                var fileLines = File.ReadAllLines(cacheFile);
                                lines.Add($"! Subscription: {item.DefaultName} [{item.Id}] (Fallback)");
                                lines.AddRange(fileLines);
                                lines.Add(string.Empty);
                                _ruleCounts[item.Id] = CountRules(fileLines);
                            }
                        }
                    }
                    else
                    {
                        if (File.Exists(cacheFile))
                        {
                            _ruleCounts[item.Id] = CountRulesInFile(cacheFile);
                        }
                    }
                }
            }

            var combinedTemporaryFile = _combinedRulesPath + ".merge-" + Guid.NewGuid().ToString("N");
            try
            {
                await File.WriteAllLinesAsync(combinedTemporaryFile, lines, new UTF8Encoding(false));
                File.Move(combinedTemporaryFile, _combinedRulesPath, true);
            }
            finally
            {
                try { if (File.Exists(combinedTemporaryFile)) File.Delete(combinedTemporaryFile); } catch { }
            }
            return CountRules(lines);
        }
        finally
        {
            _mergeSemaphore.Release();
        }
    }

    public string GetActiveCombinedFilterPath() => _combinedRulesPath;

    public string GetCacheFilePath(string filterId)
    {
        var norm = NormalizeFilterId(filterId);
        return Path.Combine(_cacheDir, $"{norm}.txt");
    }

    public static int CountRules(IEnumerable<string> lines)
    {
        var count = 0;
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;
            if (trimmed.StartsWith('!') || trimmed.StartsWith('[')) continue;
            count++;
        }
        return count;
    }

    public static int CountRulesInFile(string filePath)
    {
        if (!File.Exists(filePath)) return 0;
        try
        {
            return CountRules(File.ReadLines(filePath));
        }
        catch
        {
            return 0;
        }
    }

    public static string NormalizeFilterId(string id) => id switch
    {
        "abpvn" => "reg-vn",
        "adguard-chinese" => "reg-cn",
        "easylist-germany" => "reg-de",
        "adguard-japanese" => "reg-jp",
        "korean-list" => "reg-kr",
        "adguard-french" => "reg-fr",
        "adguard-spanish" => "reg-es-pt",
        "adguard-russian" => "reg-ru-adlist",
        "fanboy-cookiemonster" => "easylist-cookies",
        "fanboy-annoyance" => "easylist-other-annoyances",
        _ => id
    };

    private void MigrateLegacyCacheFileIfNeeded(string filterId, string targetCacheFile)
    {
        if (File.Exists(targetCacheFile)) return;

        var legacyNames = filterId switch
        {
            "youtube-adblock" => new[] { "youtube.txt", "youtube_rules.txt" },
            "reg-vn" or "abpvn" => new[] { "abpvn.txt", "abpvn_basic.txt" },
            "easylist" => new[] { "easylist.txt", "easylist_basic.txt" },
            "easyprivacy" => new[] { "easyprivacy.txt" },
            "reg-cn" => new[] { "adguard-chinese.txt", "adguard_chinese.txt" },
            "reg-de" => new[] { "easylist-germany.txt", "easylist_germany.txt" },
            "reg-jp" => new[] { "adguard-japanese.txt", "adguard_japanese.txt" },
            "reg-kr" => new[] { "korean-list.txt", "korean_list.txt" },
            "reg-fr" => new[] { "adguard-french.txt", "adguard_french.txt" },
            "reg-es-pt" => new[] { "adguard-spanish.txt", "adguard_spanish.txt" },
            "reg-ru-adlist" => new[] { "adguard-russian.txt", "adguard_russian.txt" },
            "easylist-cookies" => new[] { "fanboy-cookiemonster.txt", "fanboy_cookiemonster.txt" },
            "easylist-other-annoyances" => new[] { "fanboy-annoyance.txt", "fanboy_annoyance.txt" },
            _ => Array.Empty<string>()
        };

        foreach (var legacyName in legacyNames)
        {
            var legacyCache = Path.Combine(_cacheDir, legacyName);
            if (File.Exists(legacyCache))
            {
                try
                {
                    File.Copy(legacyCache, targetCacheFile, true);
                    return;
                }
                catch { }
            }

            var legacyRoot = Path.Combine(_filtersDir, legacyName);
            if (File.Exists(legacyRoot))
            {
                try
                {
                    File.Copy(legacyRoot, targetCacheFile, true);
                    return;
                }
                catch { }
            }
        }
    }

    private void PreloadCachedRuleCounts()
    {
        foreach (var item in FilterCatalog.Items)
        {
            var cacheFile = GetCacheFilePath(item.Id);
            if (File.Exists(cacheFile))
            {
                _ruleCounts[item.Id] = CountRulesInFile(cacheFile);
            }
            else
            {
                var fallback = Path.Combine(_filtersDir, item.FallbackFileName);
                if (File.Exists(fallback))
                {
                    _ruleCounts[item.Id] = CountRulesInFile(fallback);
                }
            }
        }
    }

    private void LoadSettings()
    {
        lock (_sync)
        {
            _enabledFilterIds.Clear();
            try
            {
                if (File.Exists(_settingsPath))
                {
                    var json = File.ReadAllText(_settingsPath);
                    var model = JsonSerializer.Deserialize<FilterSettingsModel>(json);
                    if (model?.EnabledFilters != null && model.EnabledFilters.Count > 0)
                    {
                        foreach (var id in model.EnabledFilters)
                        {
                            var norm = NormalizeFilterId(id);
                            if (FilterCatalog.ItemsById.ContainsKey(norm))
                            {
                                _enabledFilterIds.Add(norm);
                            }
                        }
                        return;
                    }
                    else if (model?.EnabledFilters != null &&
                             model.EnabledFilters.Count == 0 &&
                             model.CurrentPreset == BlockingPreset.Custom)
                    {
                        // An explicit empty Custom selection is valid user state.
                        // Do not reinterpret it as a missing/corrupt settings file.
                        return;
                    }
                    else if (model?.CurrentPreset.HasValue == true && model.CurrentPreset.Value != BlockingPreset.Custom)
                    {
                        var presetIds = FilterCatalog.GetFilterIdsForPreset(model.CurrentPreset.Value);
                        foreach (var id in presetIds)
                        {
                            _enabledFilterIds.Add(id);
                        }
                        return;
                    }
                }
            }
            catch { }

            // Apply defaults
            foreach (var id in FilterCatalog.DefaultEnabledFilterIds)
            {
                _enabledFilterIds.Add(id);
            }
        }
    }

    private void SaveSettings()
    {
        lock (_sync)
        {
            try
            {
                var dir = Path.GetDirectoryName(_settingsPath);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var model = new FilterSettingsModel
                {
                    EnabledFilters = _enabledFilterIds.OrderBy(id => id).ToList(),
                    LastUpdatedUtc = DateTime.UtcNow,
                    CurrentPreset = GetCurrentPreset()
                };
                var json = JsonSerializer.Serialize(model, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_settingsPath, json);
            }
            catch { }
        }
    }

    private static string ResolveFiltersDirectory(string? overrideDir)
    {
        if (!string.IsNullOrWhiteSpace(overrideDir))
        {
            return Path.GetFullPath(overrideDir);
        }

        var root = FindRepositoryRoot();
        if (Directory.Exists(Path.Combine(root, "filters")))
        {
            return Path.Combine(root, "filters");
        }

        return Path.Combine(AppContext.BaseDirectory, "filters");
    }

    private static string FindRepositoryRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "ChromeNativeAdblock.slnx")) ||
                File.Exists(Path.Combine(dir, "Cargo.toml")) ||
                Directory.Exists(Path.Combine(dir, "filters")))
            {
                return dir;
            }
            var parent = Directory.GetParent(dir);
            if (parent == null) break;
            dir = parent.FullName;
        }
        return AppContext.BaseDirectory;
    }
}
