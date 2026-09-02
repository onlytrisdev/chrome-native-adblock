using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChromeNativeAdblock.Launcher;

public sealed record TargetInfo(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("webSocketDebuggerUrl")] string? WebSocketDebuggerUrl);

public sealed class CosmeticInjector : IDisposable
{
    public event Action<string, string, string>? OnConsoleMessage;

    private sealed class ActiveTabSession : IAsyncDisposable
    {
        public required string TargetId { get; init; }
        public required string Domain { get; set; }
        public required string Url { get; set; }
        public required ClientWebSocket WebSocket { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        public required Task ReceiveTask { get; set; }

        public async ValueTask DisposeAsync()
        {
            Cts.Cancel();
            try
            {
                if (WebSocket.State == WebSocketState.Open)
                {
                    await WebSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                }
            }
            catch { }
            try { WebSocket.Dispose(); } catch { }
            try { Cts.Dispose(); } catch { }
        }
    }

    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<string, ActiveTabSession> _activeSessions = new();

    public CosmeticInjector()
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    }

    public static readonly string[] DefaultContainerCollapserSelectors =
    [
        "iframe[src=\"about:blank\"]",
        "iframe[src=\"\"]",
        "iframe:not([src])",
        "div[id*=\"google_ads\" i]",
        "div[id*=\"ad-container\" i]",
        "div[id*=\"adv-zone\" i]",
        "div[class*=\"box-category-adv\" i]",
        "div[class*=\"banner-ads\" i]",
        "div[class*=\"wrapper-ad\" i]",
        "div[class*=\"block-ad\" i]",
        "div[class*=\"sticky-ad\" i]",
        "div[id*=\"sticky-ad\" i]",
        ".adsbygoogle",
        ".ad-slot",
        ".ad-banner",
        ".advertisement",
        "[data-ad]",
        "[data-ad-unit]",
        "[data-ad-slot]",
        "[data-google-query-id]",
        "#banner_top",
        ".box-category-adv",
        ".banner-center",
        "#box_ad_middle",
        ".box-ad",
        ".sticky-banner",
        ".inner-adv",
        ".zone-ad",
        ".wrapper_ad",
        ".ad_container",
        ".adv-box",
        ".block_ad",
        ".banner_ad",
        ".adv-sticky",
        "div[id^=\"ad_\"]",
        "div[id*=\"_ad_\"]",
        "div[id^=\"_ads_\"]",
        "div[id*=\"_ads_\"]",
        "div[id^=\"ads_\"]",
        "div[id*=\"ads_\"]",
        "div[class*=\"adv_\"]",
        "div[class*=\"_adv_\"]",
        "div[data-role=\"ad\"]",
        "#_ads_bg_top",
        "#rich-media-banner-ads",
        "#_ads_box_business",
        "#_footer_ads"
    ];

    public static readonly string[] DefaultVietnameseNewsSelectors =
    [
        "#banner_top",
        ".banner_top",
        ".box-category-adv",
        ".banner-center",
        "#box_ad_middle",
        ".box-ad",
        ".sticky-banner",
        ".inner-adv",
        ".zone-ad",
        ".wrapper_ad",
        ".ad_container",
        ".adv-box",
        ".block_ad",
        ".banner_ad",
        ".adv-sticky",
        "#admbackground",
        "#rich-media-banner-ads",
        "#supper_masthead",
        "div[id^=\"ads_\"]",
        "div[id^=\"ad_\"]",
        "div[id*=\"_ad_\"]",
        "div[id^=\"_ads_\"]",
        "div[id*=\"_ads_\"]",
        "div[class*=\"box-category-adv\"]",
        "div[class*=\"banner-ads\"]",
        "div[class*=\"wrapper-ad\"]",
        "div[class*=\"adv-\"]",
        ".adv-holder",
        ".box_ad_bottom",
        ".banner-ads",
        "div[data-role=\"ad\"]",
        "#_ads_bg_top",
        "#_ads_box_business",
        "#_footer_ads",
        "#bannerMasthead",
        "#desktop-home-top-page",
        "#mobile-home-middle-1",
        "#mobile-home-middle-2",
        "#mobile-home-top-page",
        ".ad-container",
        ".ad-wrapper",
        "#LeaderBoardTop",
        ".banner-top",
        ".ads-sponsor",
        ".adm-banner",
        ".vmcadszone",
        ".asd-headt",
        ".detail__foru",
        "#Zingnews_SiteHeader",
        ".banner-slider-wrapper",
        ".znews-banner",
        "#background_bg_link",
        "#subRightAboveHome",
        ".adv-24h-mid"
    ];

    public static string BuildCssInjectionScript(
        IEnumerable<string> selectors,
        IEnumerable<string>? collapserSelectors = null)
    {
        var hideList = selectors.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToArray();
        var collapseList = (collapserSelectors ?? DefaultContainerCollapserSelectors)
            .Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToArray();

        if (hideList.Length == 0 && collapseList.Length == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        if (hideList.Length > 0)
        {
            var combinedHide = string.Join(",\n", hideList);
            var escapedHide = combinedHide.Replace("\\", "\\\\").Replace("`", "\\`");
            sb.Append(escapedHide);
            sb.AppendLine(" { display: none !important; }");
        }

        if (collapseList.Length > 0)
        {
            var combinedCollapse = string.Join(",\n", collapseList);
            var escapedCollapse = combinedCollapse.Replace("\\", "\\\\").Replace("`", "\\`");
            sb.Append(escapedCollapse);
            sb.AppendLine(" {\n    min-height: 0 !important;\n    height: 0 !important;\n    max-height: 0 !important;\n    margin: 0 !important;\n    padding: 0 !important;\n    border: none !important;\n    display: none !important;\n    visibility: hidden !important;\n    pointer-events: none !important;\n}");
        }

        var fullCss = sb.ToString();

        return $$"""
(function() {
    const css = `{{fullCss}}`;
    let logged = false;
    function injectStyle() {
        if (document.getElementById('cna-cosmetic-style')) return;
        const style = document.createElement('style');
        style.id = 'cna-cosmetic-style';
        style.textContent = css;
        const target = document.head || document.documentElement || document.body;
        if (target) {
            target.appendChild(style);
            if (!logged) {
                logged = true;
                try { console.info('[CNA-CSS-HIDE] Injected CSS hide style on ' + location.hostname); } catch (e) {}
            }
        }
    }
    injectStyle();
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', injectStyle);
    }
    const observer = new MutationObserver(function() {
        injectStyle();
    });
    if (document.documentElement) {
        observer.observe(document.documentElement, { childList: true, subtree: true });
    }
})();
""";
    }

    public static string BuildSmartContainerCollapserScript()
    {
        return """
(function() {
    function healLayout() {
        // 1. Collapse blocked / empty iframes
        try {
            const iframes = document.querySelectorAll('iframe:not([src]), iframe[src="about:blank"], iframe[src=""], iframe[src^="javascript:"]');
            for (let i = 0; i < iframes.length; i++) {
                collapseElement(iframes[i]);
            }
        } catch (e) {}

        // 2. Scan ad container candidate elements
        const adSelector = [
            'iframe[src="about:blank"]',
            'iframe[src=""]',
            'iframe:not([src])',
            'div[id*="google_ads" i]',
            'div[id*="ad-container" i]',
            'div[id*="adv-zone" i]',
            'div[class*="box-category-adv" i]',
            'div[class*="banner-ads" i]',
            'div[class*="wrapper-ad" i]',
            'div[class*="block-ad" i]',
            'div[class*="sticky-ad" i]',
            'div[id*="sticky-ad" i]',
            '.adsbygoogle',
            '.ad-slot',
            '.ad-banner',
            '.advertisement',
            '[data-ad]',
            '[data-ad-unit]',
            '[data-ad-slot]',
            '[data-google-query-id]',
            '#banner_top',
            '.box-category-adv',
            '.banner-center',
            '.banner-ads',
            '#box_ad_middle',
            '.box-ad',
            '.sticky-banner',
            '.inner-adv',
            '.zone-ad',
            '.wrapper_ad',
            '.ad_container',
            '.adv-box',
            '.block_ad',
            '.banner_ad',
            '.adv-sticky',
            'div[id^="ad_"]',
            'div[id*="_ad_"]',
            'div[id^="_ads_"]',
            'div[id*="_ads_"]',
            'div[id^="ads_"]',
            'div[id*="ads_"]',
            'div[class*="adv_"]',
            'div[class*="_adv_"]',
            'div[data-role="ad"]',
            '#_ads_bg_top',
            '#rich-media-banner-ads',
            '#_ads_box_business',
            '#_footer_ads'
        ].join(', ');

        try {
            const elements = document.querySelectorAll(adSelector);
            for (let i = 0; i < elements.length; i++) {
                collapseElement(elements[i]);
            }
        } catch (e) {}

        // 3. Scan potential wrapper elements with fixed heights or margins containing hidden/blocked ad children
        try {
            const wrappers = document.querySelectorAll('div, section, aside');
            for (let i = 0; i < wrappers.length; i++) {
                const w = wrappers[i];
                const id = w.id || '';
                const cls = (w.className && typeof w.className === 'string') ? w.className : '';
                const isAdLike = /(?:^|[_-])(?:ads?|adv|banner|sponsor|eclick|admicro)(?:[_-]|$)/i.test(id) ||
                                 /(?:^|[_\s-])(?:ads?|adv|banner|sponsor|eclick|admicro)(?:[_\s-]|$)/i.test(cls);

                if (isAdLike && isEffectivelyEmpty(w)) {
                    collapseElement(w);
                }
            }
        } catch (e) {}
    }

    function isEffectivelyEmpty(el) {
        if (!el) return true;
        // If element has real visible text, keep it (unless text is just ad label)
        const text = (el.innerText || '').trim();
        if (text.length > 0) {
            const isAdText = /^(?:qu[aả]ng c[aá]o|advertisement|ads?|t[aà]i tr[oợ]|đ[uư][oợ]c t[aà]i tr[oợ]|qc)$/i.test(text);
            if (!isAdText && text.length > 30) {
                return false;
            }
        }

        const children = el.children;
        if (children.length === 0) {
            return true;
        }

        for (let i = 0; i < children.length; i++) {
            const child = children[i];
            const tag = child.tagName ? child.tagName.toLowerCase() : '';
            if (tag === 'script' || tag === 'style' || tag === 'noscript' || tag === 'template') {
                continue;
            }
            if (tag === 'iframe') {
                const src = child.getAttribute('src') || '';
                if (!src || src === 'about:blank' || src.startsWith('javascript:')) {
                    continue;
                }
            }
            try {
                const style = window.getComputedStyle(child);
                if (style.display === 'none' || style.visibility === 'hidden' || style.opacity === '0') {
                    continue;
                }
            } catch (e) {}
            if (child.offsetHeight === 0 && child.offsetWidth === 0) {
                continue;
            }
            if (!isEffectivelyEmpty(child)) {
                return false;
            }
        }
        return true;
    }

    function collapseElement(el) {
        if (!el) return;
        try {
            el.style.setProperty('display', 'none', 'important');
            el.style.setProperty('min-height', '0px', 'important');
            el.style.setProperty('height', '0px', 'important');
            el.style.setProperty('max-height', '0px', 'important');
            el.style.setProperty('margin', '0px', 'important');
            el.style.setProperty('padding', '0px', 'important');
            el.style.setProperty('border', 'none', 'important');
            el.style.setProperty('visibility', 'hidden', 'important');
            el.style.setProperty('pointer-events', 'none', 'important');
        } catch (e) {}
    }

    // Run layout healer at various stages
    healLayout();
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', healLayout);
    }
    window.addEventListener('load', healLayout);

    let timeoutId = null;
    function scheduleHeal() {
        if (timeoutId) return;
        timeoutId = setTimeout(function() {
            timeoutId = null;
            healLayout();
        }, 50);
    }

    const observer = new MutationObserver(function() {
        scheduleHeal();
    });

    if (document.documentElement) {
        observer.observe(document.documentElement, { childList: true, subtree: true, attributes: true, attributeFilter: ['style', 'class'] });
    }

    setTimeout(healLayout, 100);
    setTimeout(healLayout, 300);
    setTimeout(healLayout, 600);
    setTimeout(healLayout, 1200);
    setTimeout(healLayout, 2500);
})();
""";
    }
    public static string BuildYouTubeBypassScript()
    {
        return """
(function() {
    const host = location && location.hostname ? location.hostname.toLowerCase() : '';
    if (host !== 'youtube.com' && !host.endsWith('.youtube.com')) return;
    if (window.__cnaYtSafeCleanupInstalled) return;
    window.__cnaYtSafeCleanupInstalled = true;

    // Keep this helper deliberately conservative. Do not proxy fetch/XHR/JSON,
    // rewrite player responses, or seek the main content timeline. Those
    // techniques are detectable and can leave YouTube's player stalled.
    const cosmeticSelectors = [
        '#masthead-ad',
        '#player-ads',
        'ytd-ad-slot-renderer',
        'ytd-in-feed-ad-layout-renderer',
        'ytd-display-ad-renderer',
        'ytd-promoted-video-renderer',
        'ytd-compact-promoted-video-renderer',
        'ytd-action-companion-ad-renderer',
        'ytd-banner-promo-renderer',
        'ytd-statement-banner-renderer',
        '.ytp-ad-overlay-container',
        '.ytp-ad-image-overlay',
        '.ytp-ad-overlay-image'
    ];

    const skipButtonSelectors = [
        '.ytp-ad-skip-button',
        '.ytp-skip-ad-button',
        '.ytp-ad-skip-button-modern',
        'button.ytp-ad-skip-button-modern',
        '.ytp-ad-skip-button-slot button',
        '.ytp-ad-overlay-close-button'
    ];

    function isVisible(element) {
        if (!(element instanceof HTMLElement)) return false;
        const style = getComputedStyle(element);
        return style.display !== 'none' && style.visibility !== 'hidden' &&
            element.getClientRects().length > 0;
    }

    let lastPlayerSkipAttempt = 0;
    function requestPlayerSkip() {
        const player = document.getElementById('movie_player');
        if (!player || !player.classList.contains('ad-showing')) return false;
        const now = Date.now();
        if (now - lastPlayerSkipAttempt < 1000) return false;
        lastPlayerSkipAttempt = now;

        let skipped = false;
        try {
            if (typeof player.skipAd === 'function') {
                player.skipAd();
                skipped = true;
            }
        } catch (e) {}

        // Some short unskippable pre-rolls ignore skipAd(). Advance only the
        // active ad media element; never touch a normal/long content video.
        const adVideo = player.querySelector('video');
        if (adVideo && Number.isFinite(adVideo.duration) &&
            adVideo.duration > 0 && adVideo.duration <= 120) {
            try {
                adVideo.currentTime = adVideo.duration;
                adVideo.playbackRate = 16;
                adVideo.dataset.cnaAdAccelerated = '1';
                skipped = true;
            } catch (e) {}
        }
        return skipped;
    }

    function cleanupYouTubeAds() {
        const activePlayer = document.getElementById('movie_player');
        if (activePlayer && !activePlayer.classList.contains('ad-showing')) {
            const contentVideo = activePlayer.querySelector('video[data-cna-ad-accelerated="1"]');
            if (contentVideo) {
                try { contentVideo.playbackRate = 1; } catch (e) {}
                try { delete contentVideo.dataset.cnaAdAccelerated; } catch (e) {}
            }
        }

        for (const selector of cosmeticSelectors) {
            for (const element of document.querySelectorAll(selector)) {
                try { element.remove(); } catch (e) {}
            }
        }

        let clicked = requestPlayerSkip();
        for (const selector of skipButtonSelectors) {
            for (const button of document.querySelectorAll(selector)) {
                if (!isVisible(button)) continue;
                try {
                    button.click();
                    clicked = true;
                } catch (e) {}
            }
        }

        if (clicked) {
            try { console.info('[CNA-YT-SKIP] Clicked a visible YouTube skip control'); } catch (e) {}
        }
    }

    let scheduled = false;
    function scheduleCleanup() {
        if (scheduled) return;
        scheduled = true;
        setTimeout(function() {
            scheduled = false;
            cleanupYouTubeAds();
        }, 100);
    }

    const start = function() {
        cleanupYouTubeAds();
        const root = document.documentElement;
        if (!root) return;
        new MutationObserver(scheduleCleanup).observe(root, {
            childList: true,
            subtree: true,
            attributes: true,
            attributeFilter: ['class']
        });
    };

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', start, { once: true });
    } else {
        start();
    }
})();
""";
    }

    // Kept temporarily for source-level comparison with the former v1.0.0
    // implementation. It is never injected or called.
    private static string BuildLegacyYouTubeBypassScript()
    {
        return """
(function() {
    if (!location.hostname.includes('youtube.com')) return;

    // Recursive object sanitizer to strip ad placements and player ads
    function sanitizePlayerResponse(obj) {
        if (!obj || typeof obj !== 'object') return obj;
        try {
            if (Array.isArray(obj)) {
                for (let i = 0; i < obj.length; i++) {
                    sanitizePlayerResponse(obj[i]);
                }
                return obj;
            }

            const adKeys = [
                'adPlacements', 'playerAds', 'adSlots', 'adBreakHeartbeatParams',
                'adBreakService', 'adPlacementRenderer', 'invideoAdPlacementRenderer',
                'adTag', 'adIntro', 'adSignalsInfo', 'adPlaybackContext', 'adBreak'
            ];
            let strippedCount = 0;
            for (let i = 0; i < adKeys.length; i++) {
                const k = adKeys[i];
                if (k in obj) {
                    if (Array.isArray(obj[k])) {
                        if (obj[k].length > 0) strippedCount++;
                        obj[k] = [];
                    } else if (typeof obj[k] === 'object' && obj[k] !== null) {
                        strippedCount++;
                        delete obj[k];
                    } else {
                        strippedCount++;
                        delete obj[k];
                    }
                }
            }
            if (strippedCount > 0) {
                try { console.info('[CNA-YT-SANITIZE] Stripped adPlacements / playerAds from playerResponse'); } catch (e) {}
            }
            // Strip ad URLs from playback tracking
            if (obj.playbackTracking) {
                delete obj.playbackTracking.videostatsPlaybackUrl;
                delete obj.playbackTracking.videostatsDelayplayUrl;
                delete obj.playbackTracking.videostatsWatchtimeUrl;
                delete obj.playbackTracking.ptrackingUrl;
                delete obj.playbackTracking.qoeUrl;
                delete obj.playbackTracking.atrUrl;
                delete obj.playbackTracking.videostatsEngagedWatchtimeUrl;
            }

            // Filter out ad slot renderers in browse/next section contents
            if (obj.sectionListRenderer && Array.isArray(obj.sectionListRenderer.contents)) {
                obj.sectionListRenderer.contents = obj.sectionListRenderer.contents.filter(function(item) {
                    return !item.adSlotRenderer && !item.inFeedAdLayoutRenderer;
                });
            }

            if (obj.contents && typeof obj.contents === 'object') {
                sanitizePlayerResponse(obj.contents);
            }
            if (obj.items && Array.isArray(obj.items)) {
                for (let i = 0; i < obj.items.length; i++) {
                    sanitizePlayerResponse(obj.items[i]);
                }
            }
        } catch (e) {}
        return obj;
    }

    // 1. Intercept window.ytInitialPlayerResponse
    let rawInitialPlayerResponse = window.ytInitialPlayerResponse;
    if (rawInitialPlayerResponse) {
        sanitizePlayerResponse(rawInitialPlayerResponse);
    }
    try {
        Object.defineProperty(window, 'ytInitialPlayerResponse', {
            get: function() { return rawInitialPlayerResponse; },
            set: function(val) {
                sanitizePlayerResponse(val);
                rawInitialPlayerResponse = val;
            },
            configurable: true
        });
    } catch (e) {}

    // 1b. Intercept window.ytInitialData
    let rawInitialData = window.ytInitialData;
    if (rawInitialData) {
        sanitizePlayerResponse(rawInitialData);
    }
    try {
        Object.defineProperty(window, 'ytInitialData', {
            get: function() { return rawInitialData; },
            set: function(val) {
                sanitizePlayerResponse(val);
                rawInitialData = val;
            },
            configurable: true
        });
    } catch (e) {}

    // 2. Intercept window.ytplayer and window.ytplayer.config.args.raw_player_response
    function hookYtPlayer(playerObj) {
        if (!playerObj || typeof playerObj !== 'object') return playerObj;
        try {
            if (playerObj.config && playerObj.config.args && playerObj.config.args.raw_player_response) {
                if (typeof playerObj.config.args.raw_player_response === 'string') {
                    try {
                        let parsed = JSON.parse(playerObj.config.args.raw_player_response);
                        sanitizePlayerResponse(parsed);
                        playerObj.config.args.raw_player_response = JSON.stringify(parsed);
                    } catch (err) {}
                } else if (typeof playerObj.config.args.raw_player_response === 'object') {
                    sanitizePlayerResponse(playerObj.config.args.raw_player_response);
                }
            }
        } catch (e) {}
        return playerObj;
    }
    function hookMoviePlayer(player) {
        if (!player || player._cnaHooked) return;
        player._cnaHooked = true;
        if (typeof player.getPlayerResponse === 'function') {
            const origGetPlayerResponse = player.getPlayerResponse;
            player.getPlayerResponse = function() {
                const res = origGetPlayerResponse.apply(this, arguments);
                if (res && typeof res === 'object') {
                    sanitizePlayerResponse(res);
                }
                return res;
            };
        }
    }

    let _ytplayer = window.ytplayer;
    if (_ytplayer) {
        hookYtPlayer(_ytplayer);
    }
    try {
        Object.defineProperty(window, 'ytplayer', {
            get: function() { return _ytplayer; },
            set: function(val) {
                _ytplayer = hookYtPlayer(val);
            },
            configurable: true
        });
    } catch (e) {}

    // 3. Intercept fetch for /youtubei/v1/player, /youtubei/v1/browse, /youtubei/v1/next
    const originalFetch = window.fetch;
    if (originalFetch) {
        window.fetch = async function(input, init) {
            const url = typeof input === 'string' ? input : (input && input.url ? input.url : '');
            const isYtEndpoint = typeof url === 'string' && (
                url.indexOf('/youtubei/v1/player') !== -1 ||
                url.indexOf('/youtubei/v1/browse') !== -1 ||
                url.indexOf('/youtubei/v1/next') !== -1
            );

            const response = await originalFetch.apply(this, arguments);
            if (!isYtEndpoint) {
                return response;
            }

            try {
                const clone = response.clone();
                const text = await clone.text();
                if (text && text.charCodeAt(0) === 123) { // starts with '{'
                    const data = JSON.parse(text);
                    sanitizePlayerResponse(data);
                    const modified = JSON.stringify(data);
                    return new Response(modified, {
                        status: response.status,
                        statusText: response.statusText,
                        headers: response.headers
                    });
                }
            } catch (e) {}
            return response;
        };
    }

    // 4. Intercept XMLHttpRequest for /youtubei/v1/player, /youtubei/v1/browse, /youtubei/v1/next
    const origOpen = XMLHttpRequest.prototype.open;
    const origSend = XMLHttpRequest.prototype.send;
    XMLHttpRequest.prototype.open = function(method, url) {
        this._cnaUrl = typeof url === 'string' ? url : '';
        return origOpen.apply(this, arguments);
    };
    XMLHttpRequest.prototype.send = function() {
        if (this._cnaUrl && (
            this._cnaUrl.indexOf('/youtubei/v1/player') !== -1 ||
            this._cnaUrl.indexOf('/youtubei/v1/browse') !== -1 ||
            this._cnaUrl.indexOf('/youtubei/v1/next') !== -1
        )) {
            this.addEventListener('readystatechange', function() {
                if (this.readyState === 4) {
                    try {
                        const responseText = this.responseText;
                        if (responseText && responseText.charCodeAt(0) === 123) {
                            const json = JSON.parse(responseText);
                            sanitizePlayerResponse(json);
                            const sanitizedText = JSON.stringify(json);
                            Object.defineProperty(this, 'responseText', { value: sanitizedText, configurable: true });
                            Object.defineProperty(this, 'response', { value: sanitizedText, configurable: true });
                        }
                    } catch (e) {}
                }
            });
        }
        return origSend.apply(this, arguments);
    };

    // 5. Intercept JSON.parse
    const originalParse = JSON.parse;
    JSON.parse = function() {
        const result = originalParse.apply(this, arguments);
        if (result && typeof result === 'object') {
            sanitizePlayerResponse(result);
        }
        return result;
    };

    // 6. Continuous ultra-fast video ad skipper (every 25ms + on video events)
    let lastSkipLog = 0;
    function logSkip() {
        const now = Date.now();
        if (now - lastSkipLog > 1000) {
            lastSkipLog = now;
            try { console.info('[CNA-YT-SKIP] Fast-skipped video ad & clicked skip button'); } catch (e) {}
        }
    }

    function skipVideoAds() {
        try {
            let didSkip = false;

            // 1. YouTube internal player API skip & seek
            const player = document.getElementById('movie_player') || document.querySelector('.html5-video-player');
            if (player) {
                hookMoviePlayer(player);
                if (typeof player.skipAd === 'function') {
                    try { player.skipAd(); didSkip = true; } catch(e){}
                }
                if (typeof player.seekTo === 'function' && (document.querySelector('.ad-showing, .ytp-ad-showing, .ytp-ad-player-overlay'))) {
                    try {
                        const dur = typeof player.getDuration === 'function' ? player.getDuration() : 9999;
                        player.seekTo(dur, true);
                        didSkip = true;
                    } catch(e){}
                }
            }

            // 2. Click all modern skip buttons & dismiss badges
            const skipButtons = document.querySelectorAll(
                '.ytp-ad-skip-button, .ytp-skip-ad-button, .ytp-ad-skip-button-modern, ' +
                'button.ytp-ad-skip-button-modern, .ytp-ad-skip-button-slot, ' +
                '.ytp-ad-overlay-close-button, .ytp-ad-skip-button-container button, ' +
                '.ytp-ad-action-interstitial-action-button, .ytp-suggested-action-badge, ' +
                '.ytp-ad-action-interstitial-action-button button'
            );
            for (let i = 0; i < skipButtons.length; i++) {
                try { skipButtons[i].click(); didSkip = true; } catch (e) {}
            }

            // 3. Detect playing ad and fast-forward
            const isAdShowing = document.querySelector('.ad-showing, .ytp-ad-showing, .ytp-ad-player-overlay') !== null;
            const videos = document.querySelectorAll('video');
            for (let i = 0; i < videos.length; i++) {
                const video = videos[i];
                const isShortAd = !isNaN(video.duration) && video.duration > 0 && video.duration < 60;
                const isAd = isAdShowing || (isShortAd && video.closest('.ad-showing, .ytp-ad-showing, .video-ads, .html5-video-player'));
                if (isAd) {
                    video.muted = true;
                    video.playbackRate = 16.0;
                    if (!isNaN(video.duration) && video.duration > 0) {
                        video.currentTime = video.duration || 9999;
                        didSkip = true;
                    }
                }
            }

            // Clear ad-showing class on player if main video is active
            if (player && (player.classList.contains('ad-showing') || player.classList.contains('ytp-ad-showing'))) {
                const v = player.querySelector('video');
                if (v && !isNaN(v.duration) && v.duration > 45 && !v.paused) {
                    player.classList.remove('ad-showing');
                    player.classList.remove('ytp-ad-showing');
                }
            }

            if (didSkip) {
                logSkip();
            }

            // 4. Remove overlay elements, interstitial layouts & promo banners
            const overlaySelectors = [
                '.ytp-ad-action-interstitial', '.ytp-ad-action-interstitial-background',
                '.ytp-ad-image-overlay', '.ytp-ad-overlay-image',
                '.ytp-ad-player-overlay-layout', '.ytp-ad-player-overlay-flyout-cta',
                '.ytp-ad-player-overlay-instream-user-sentiment',
                'ytd-action-companion-ad-renderer', '.ytp-ad-overlay-slot',
                '.ytp-ad-overlay-container', '.video-ads.ytp-ad-module',
                'div#action-companion-click-target', '.ytp-suggested-action-badge',
                '.ytp-ad-action-interstitial-action-button', '.ytp-ad-module',
                'ytd-ad-slot-renderer', 'ytd-in-feed-ad-layout-renderer',
                '#player-ads', '#masthead-ad', 'ytd-banner-promo-renderer',
                'ytd-statement-banner-renderer', 'ytd-display-ad-renderer',
                '.ytd-promoted-video-renderer', '.ytd-compact-promoted-video-renderer',
                '.ytd-promoted-sparkles-web-renderer', '.ytd-promoted-sparkles-text-search-renderer',
                '.ytp-ad-text', '.ytp-ad-preview-container', '.ytp-ad-player-overlay', '.ytp-ad-survey'
            ];
            const overlays = document.querySelectorAll(overlaySelectors.join(', '));
            for (let i = 0; i < overlays.length; i++) {
                try { overlays[i].remove(); } catch (e) {}
            }
        } catch (e) {}
    }

    function ensureYtStyle() {
        if (!document.getElementById('cna-yt-inline-style') && (document.head || document.documentElement)) {
            const style = document.createElement('style');
            style.id = 'cna-yt-inline-style';
            style.textContent = `
            .ytp-ad-action-interstitial, .ytp-ad-action-interstitial-background,
            .ytp-ad-image-overlay, .ytp-ad-overlay-image,
            .ytp-ad-player-overlay-layout, .ytp-ad-player-overlay-flyout-cta,
            .ytp-ad-player-overlay-instream-user-sentiment,
            ytd-action-companion-ad-renderer, .ytp-ad-overlay-slot,
            .ytp-ad-overlay-container, .video-ads.ytp-ad-module,
            div#action-companion-click-target, .ytp-suggested-action-badge,
            .ytp-ad-action-interstitial-action-button, .ytp-ad-module,
            ytd-ad-slot-renderer, ytd-in-feed-ad-layout-renderer,
            #player-ads, #masthead-ad, ytd-banner-promo-renderer,
            ytd-statement-banner-renderer, ytd-display-ad-renderer,
            .ytd-promoted-video-renderer, .ytd-compact-promoted-video-renderer,
            .ytd-promoted-sparkles-web-renderer, .ytd-promoted-sparkles-text-search-renderer,
            .ytp-ad-text, .ytp-ad-preview-container, .ytp-ad-player-overlay, .ytp-ad-survey {
                display: none !important;
                opacity: 0 !important;
                pointer-events: none !important;
                visibility: hidden !important;
                height: 0 !important;
                width: 0 !important;
            }
            `;
            (document.head || document.documentElement).appendChild(style);
        }
    }
    ensureYtStyle();

    function bindVideoEvents() {
        const videos = document.querySelectorAll('video');
        for (let i = 0; i < videos.length; i++) {
            const video = videos[i];
            if (!video._cnaBound) {
                video._cnaBound = true;
                const events = ['timeupdate', 'play', 'loadstart', 'playing', 'progress'];
                for (let j = 0; j < events.length; j++) {
                    video.addEventListener(events[j], skipVideoAds, { passive: true });
                }
            }
        }
    }

    // Fast interval polling
    setInterval(function() {
        ensureYtStyle();
        skipVideoAds();
        bindVideoEvents();
    }, 25);

    // MutationObserver for immediate removal on appearance
    const ytObserver = new MutationObserver(function() {
        ensureYtStyle();
        skipVideoAds();
    });
    if (document.documentElement) {
        ytObserver.observe(document.documentElement, { childList: true, subtree: true });
    } else {
        document.addEventListener('DOMContentLoaded', function() {
            if (document.documentElement) {
                ytObserver.observe(document.documentElement, { childList: true, subtree: true });
            }
        });
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', function() {
            ensureYtStyle();
            skipVideoAds();
            bindVideoEvents();
        });
    }
})();
""";
    }

    public static string BuildFullInjectionScript(NativeEngine engine)
    {
        var sb = new StringBuilder();
        var allSelectors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var injectedScripts = new List<string>();

        // 1. YouTube cosmetic extraction
        var ytCosmetic = engine.GetUrlCosmeticResources("https://www.youtube.com/watch?v=123");
        foreach (var s in ytCosmetic.HideSelectors) allSelectors.Add(s);
        if (!string.IsNullOrWhiteSpace(ytCosmetic.InjectedScript)) injectedScripts.Add(ytCosmetic.InjectedScript);

        // Ensure standard YouTube ad selectors are present
        var defaultYtSelectors = new[]
        {
            ".ytd-ad-slot-renderer",
            "ytd-in-feed-ad-layout-renderer",
            ".ytp-ad-module",
            ".ytp-ad-overlay-container",
            "#player-ads",
            "#masthead-ad",
            "ytd-banner-promo-renderer",
            "ytd-statement-banner-renderer",
            ".ytd-promoted-video-renderer",
            ".ytd-compact-promoted-video-renderer",
            "ytd-promoted-sparkles-web-renderer",
            "ytd-promoted-sparkles-text-search-renderer",
            ".ytp-ad-text",
            ".ytp-ad-preview-container",
            ".ytp-ad-player-overlay",
            ".ytp-ad-player-overlay-flyout-cta",
            ".ytp-ad-survey",
            "ytd-display-ad-renderer",
            ".ytp-ad-action-interstitial",
            ".ytp-ad-action-interstitial-background",
            ".ytp-ad-image-overlay",
            ".ytp-ad-overlay-image",
            ".ytp-ad-player-overlay-layout",
            ".ytp-ad-player-overlay-instream-user-sentiment",
            "ytd-action-companion-ad-renderer",
            ".ytp-ad-overlay-slot",
            ".video-ads.ytp-ad-module",
            "div#action-companion-click-target",
            ".ytp-suggested-action-badge",
            ".ytp-ad-action-interstitial-action-button"
        };
        foreach (var sel in defaultYtSelectors)
        {
            allSelectors.Add(sel);
        }

        // 2. Generic cosmetic rules
        var genericCosmetic = engine.GetUrlCosmeticResources("https://example.com/");
        foreach (var s in genericCosmetic.HideSelectors) allSelectors.Add(s);
        if (!string.IsNullOrWhiteSpace(genericCosmetic.InjectedScript)) injectedScripts.Add(genericCosmetic.InjectedScript);

        // 3. Vietnamese news domains cosmetic rules
        var vietnameseDomains = new[]
        {
            "https://vnexpress.net/",
            "https://dantri.com.vn/",
            "https://tuoitre.vn/",
            "https://kenh14.vn/",
            "https://vietnamnet.vn/",
            "https://thanhnien.vn/",
            "https://znews.vn/",
            "https://www.24h.com.vn/"
        };

        foreach (var domain in vietnameseDomains)
        {
            var cosmetic = engine.GetUrlCosmeticResources(domain);
            foreach (var s in cosmetic.HideSelectors) allSelectors.Add(s);
            if (!string.IsNullOrWhiteSpace(cosmetic.InjectedScript)) injectedScripts.Add(cosmetic.InjectedScript);
        }

        // Ensure default Vietnamese news ad selectors are present
        foreach (var sel in DefaultVietnameseNewsSelectors)
        {
            allSelectors.Add(sel);
        }

        // 4. Build CSS injection script containing both hide rules and deep container collapser rules
        var cssScript = BuildCssInjectionScript(allSelectors, DefaultContainerCollapserSelectors);
        if (!string.IsNullOrEmpty(cssScript))
        {
            sb.AppendLine(cssScript);
        }

        // 5. Injected scriptlets from engine
        foreach (var script in injectedScripts.Distinct())
        {
            sb.AppendLine(script);
        }

        // 6. YouTube bypass helper scriptlet
        sb.AppendLine(BuildYouTubeBypassScript());

        // 7. Smart Container Collapser JS (Layout Healer)
        sb.AppendLine(BuildSmartContainerCollapserScript());

        return sb.ToString();
    }

    public static string BuildInjectionScriptForUrl(NativeEngine engine, string pageUrl)
    {
        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var pageUri) ||
            (pageUri.Scheme != Uri.UriSchemeHttp && pageUri.Scheme != Uri.UriSchemeHttps))
        {
            return string.Empty;
        }

        var resources = engine.GetUrlCosmeticResources(pageUri.AbsoluteUri);
        var selectors = new HashSet<string>(resources.HideSelectors, StringComparer.OrdinalIgnoreCase);
        var isYouTube = pageUri.Host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase) ||
                        pageUri.Host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase);
        var vietnameseNewsHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "vnexpress.net", "www.vnexpress.net", "dantri.com.vn", "www.dantri.com.vn",
            "tuoitre.vn", "www.tuoitre.vn", "kenh14.vn", "www.kenh14.vn",
            "vietnamnet.vn", "www.vietnamnet.vn", "thanhnien.vn", "www.thanhnien.vn",
            "znews.vn", "www.znews.vn", "24h.com.vn", "www.24h.com.vn"
        };
        var isVietnameseNews = vietnameseNewsHosts.Contains(pageUri.Host);

        if (isVietnameseNews)
        {
            selectors.UnionWith(DefaultVietnameseNewsSelectors);
        }

        var conservativeCollapsers = new[]
        {
            "iframe[src=\"about:blank\"]", "iframe[src=\"\"]", "iframe:not([src])",
            "div[id*=\"google_ads\" i]", ".adsbygoogle", ".ad-slot", ".advertisement",
            "[data-ad]", "[data-ad-unit]", "[data-ad-slot]", "[data-google-query-id]"
        };

        var payload = new StringBuilder();
        var css = BuildCssInjectionScript(
            selectors,
            isVietnameseNews ? DefaultContainerCollapserSelectors : conservativeCollapsers);
        if (!string.IsNullOrWhiteSpace(css)) payload.AppendLine(css);
        // uBO subscriptions currently include YouTube response-pruning scriptlets
        // that replace JSON.parse globally. YouTube detects that mutation and can
        // stall the player behind its anti-adblock dialog. Keep network and
        // cosmetic filtering active, but do not execute subscription scriptlets
        // in YouTube's page context.
        if (!isYouTube && !string.IsNullOrWhiteSpace(resources.InjectedScript))
        {
            payload.AppendLine(resources.InjectedScript);
        }
        if (isYouTube) payload.AppendLine(BuildYouTubeBypassScript());
        if (isVietnameseNews) payload.AppendLine(BuildSmartContainerCollapserScript());

        if (payload.Length == 0) return string.Empty;

        var expectedHostJson = JsonSerializer.Serialize(pageUri.Host);
        return $$"""
(function() {
    if (!location || location.hostname.toLowerCase() !== {{expectedHostJson}}.toLowerCase()) return;
{{payload}}
})();
""";
    }


    public async Task<int> DiscoverAndInjectAsync(int port, string script, CancellationToken cancellationToken = default)
        => await DiscoverAndInjectAsync(port, _ => script, cancellationToken);

    public async Task<int> DiscoverAndInjectAsync(int port, Func<string, string> scriptFactory, CancellationToken cancellationToken = default)
    {
        var targets = await GetPageTargetsAsync(port, cancellationToken);
        var injectedCount = 0;
        var activeTargetIds = new HashSet<string>(targets.Select(t => t.Id));

        // 1. Clean up sessions for closed tabs
        foreach (var (targetId, session) in _activeSessions)
        {
            if (!activeTargetIds.Contains(targetId) || session.WebSocket.State != WebSocketState.Open)
            {
                if (_activeSessions.TryRemove(targetId, out var removedSession))
                {
                    await removedSession.DisposeAsync();
                }
            }
        }

        // 2. Attach and inject into new or unhooked page targets
        foreach (var target in targets)
        {
            if (string.IsNullOrEmpty(target.WebSocketDebuggerUrl)) continue;

            if (_activeSessions.TryGetValue(target.Id, out var existingSession) &&
                existingSession.WebSocket.State == WebSocketState.Open &&
                string.Equals(existingSession.Url, target.Url, StringComparison.Ordinal))
            {
                continue;
            }

            if (_activeSessions.TryRemove(target.Id, out var staleSession))
            {
                await staleSession.DisposeAsync();
            }

            var script = scriptFactory(target.Url);
            if (string.IsNullOrWhiteSpace(script)) continue;

            try
            {
                var ws = new ClientWebSocket();
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                connectCts.CancelAfter(TimeSpan.FromSeconds(5));
                await ws.ConnectAsync(new Uri(target.WebSocketDebuggerUrl), connectCts.Token);

                // Enable Page domain
                var enablePage = JsonSerializer.SerializeToUtf8Bytes(new { id = 1, method = "Page.enable" });
                await ws.SendAsync(enablePage, WebSocketMessageType.Text, true, cancellationToken);

                // Add script to evaluate on new document
                var addScript = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    id = 2,
                    method = "Page.addScriptToEvaluateOnNewDocument",
                    @params = new { source = script }
                });
                await ws.SendAsync(addScript, WebSocketMessageType.Text, true, cancellationToken);

                // Enable Runtime domain
                var enableRuntime = JsonSerializer.SerializeToUtf8Bytes(new { id = 3, method = "Runtime.enable" });
                await ws.SendAsync(enableRuntime, WebSocketMessageType.Text, true, cancellationToken);

                // Evaluate immediately on current document
                var evalScript = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    id = 4,
                    method = "Runtime.evaluate",
                    @params = new { expression = script }
                });
                await ws.SendAsync(evalScript, WebSocketMessageType.Text, true, cancellationToken);

                var domain = "unknown";
                try
                {
                    if (!string.IsNullOrEmpty(target.Url) && Uri.TryCreate(target.Url, UriKind.Absolute, out var uri))
                    {
                        domain = uri.Host;
                    }
                }
                catch { }

                var sessionCts = new CancellationTokenSource();
                var newSession = new ActiveTabSession
                {
                    TargetId = target.Id,
                    Domain = domain,
                    Url = target.Url,
                    WebSocket = ws,
                    Cts = sessionCts,
                    ReceiveTask = Task.CompletedTask
                };

                newSession.ReceiveTask = Task.Run(() => RunReceiveLoopAsync(newSession, sessionCts.Token), sessionCts.Token);

                if (_activeSessions.TryRemove(target.Id, out var oldSession))
                {
                    await oldSession.DisposeAsync();
                }
                _activeSessions[target.Id] = newSession;
                injectedCount++;
            }
            catch (Exception)
            {
                // Target may have navigated or closed during connection attempt
            }
        }

        return injectedCount;
    }

    private async Task RunReceiveLoopAsync(ActiveTabSession session, CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var ms = new MemoryStream();
        while (!ct.IsCancellationRequested && session.WebSocket.State == WebSocketState.Open)
        {
            WebSocketReceiveResult res;
            try
            {
                res = await session.WebSocket.ReceiveAsync(buffer, ct);
                if (res.MessageType == WebSocketMessageType.Close) break;
                ms.Write(buffer, 0, res.Count);
                if (!res.EndOfMessage) continue;
            }
            catch
            {
                break;
            }

            var messageBytes = ms.ToArray();
            ms.SetLength(0);

            if (res.MessageType == WebSocketMessageType.Text && messageBytes.Length > 0)
            {
                try
                {
                    using var doc = JsonDocument.Parse(messageBytes);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("method", out var methodProp) &&
                        methodProp.GetString() == "Runtime.consoleAPICalled" &&
                        root.TryGetProperty("params", out var paramsProp))
                    {
                        var type = paramsProp.TryGetProperty("type", out var typeProp) ? typeProp.GetString() ?? "info" : "info";
                        if (paramsProp.TryGetProperty("args", out var argsProp) && argsProp.ValueKind == JsonValueKind.Array)
                        {
                            var parts = new List<string>();
                            foreach (var arg in argsProp.EnumerateArray())
                            {
                                if (arg.TryGetProperty("value", out var valProp))
                                {
                                    parts.Add(valProp.GetString() ?? valProp.ToString());
                                }
                                else if (arg.TryGetProperty("description", out var descProp))
                                {
                                    parts.Add(descProp.GetString() ?? descProp.ToString());
                                }
                            }
                            var text = string.Join(" ", parts);
                            OnConsoleMessage?.Invoke(session.Domain, type, text);
                        }
                    }
                }
                catch
                {
                    // Ignore parsing error on unexpected frames
                }
            }
        }
    }

    public async Task<IReadOnlyList<TargetInfo>> GetPageTargetsAsync(int port, CancellationToken cancellationToken = default)
    {
        try
        {
            var targets = await _httpClient.GetFromJsonAsync<List<TargetInfo>>($"http://127.0.0.1:{port}/json/list", cancellationToken);
            return targets?.Where(t => string.Equals(t.Type, "page", StringComparison.OrdinalIgnoreCase)).ToArray()
                   ?? Array.Empty<TargetInfo>();
        }
        catch (Exception)
        {
            return Array.Empty<TargetInfo>();
        }
    }

    public async Task StartMonitoringAsync(
        Func<int?> getPortFunc,
        string script,
        CancellationToken cancellationToken)
        => await StartMonitoringAsync(getPortFunc, _ => script, cancellationToken);

    public async Task StartMonitoringAsync(
        Func<int?> getPortFunc,
        Func<string, string> scriptFactory,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var port = getPortFunc();
                if (port.HasValue && port.Value > 0)
                {
                    await DiscoverAndInjectAsync(port.Value, scriptFactory, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                // Resilient to browser restarts / transient CDP drops
            }

            try
            {
                await Task.Delay(200, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public void Dispose()
    {
        foreach (var (_, session) in _activeSessions)
        {
            try { session.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
        }
        _activeSessions.Clear();
        _httpClient.Dispose();
    }
}
