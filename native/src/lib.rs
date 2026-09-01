use adblock::Engine;
use adblock::lists::{FilterSet, ParseOptions};
use adblock::request::Request;
use adblock::resources::{MimeType, PermissionMask, Resource, ResourceType};
use serde::{Deserialize, Serialize};
use std::fs;
use std::slice;
use std::str;
use std::sync::{LazyLock, RwLock};

#[cfg(windows)]
mod network_hook;

pub const CNA_OK: i32 = 0;
pub const CNA_BLOCK: i32 = 1;
pub const CNA_ALLOW: i32 = 0;
pub const CNA_ERROR: i32 = -1;
const VERSION: &[u8] = b"chrome-native-adblock/1.0.0 adblock-rust/0.13.3\0";

static ENGINE: LazyLock<RwLock<Option<Engine>>> = LazyLock::new(|| RwLock::new(None));
static LAST_ERROR: LazyLock<RwLock<String>> = LazyLock::new(|| RwLock::new(String::new()));

fn engine_slot() -> &'static RwLock<Option<Engine>> {
    &ENGINE
}

fn error_slot() -> &'static RwLock<String> {
    &LAST_ERROR
}

fn set_error(message: impl Into<String>) -> i32 {
    let msg = message.into();
    if let Ok(mut slot) = error_slot().write() {
        *slot = msg;
    }
    CNA_ERROR
}

fn clear_error() {
    if let Ok(mut slot) = error_slot().write() {
        slot.clear();
    }
}

fn make_scriptlet_resource(name: &str, aliases: &[&str], js_template: &str) -> Resource {
    Resource {
        name: name.to_string(),
        aliases: aliases.iter().map(|s| s.to_string()).collect(),
        kind: ResourceType::Mime(MimeType::ApplicationJavascript),
        content: base64::Engine::encode(
            &base64::engine::general_purpose::STANDARD,
            js_template.as_bytes(),
        ),
        dependencies: vec![],
        permission: PermissionMask::from_bits(0),
    }
}

pub fn default_scriptlet_resources() -> Vec<Resource> {
    vec![
        make_scriptlet_resource(
            "set-constant",
            &["set-constant.js", "set.js", "set_constant", "set"],
            r#"(function() {
    var prop = '{{1}}';
    var val = '{{2}}';
    if (val === 'undefined') val = undefined;
    else if (val === 'false') val = false;
    else if (val === 'true') val = true;
    else if (val === 'null') val = null;
    else if (val === 'noopFunc' || val === 'noopfn') val = function() {};
    else if (val === 'trueFunc') val = function() { return true; };
    else if (val === 'falseFunc') val = function() { return false; };
    else if (/^-?\d+$/.test(val)) val = parseInt(val, 10);
    try {
        var parts = prop.split('.');
        var target = window;
        for (var i = 0; i < parts.length - 1; i++) {
            if (!(parts[i] in target)) target[parts[i]] = {};
            target = target[parts[i]];
        }
        var last = parts[parts.length - 1];
        Object.defineProperty(target, last, {
            get: function() { return val; },
            set: function() {},
            configurable: true
        });
    } catch (e) {}
})();"#,
        ),
        make_scriptlet_resource(
            "abort-current-inline-script",
            &[
                "abort-current-inline-script.js",
                "acis.js",
                "acis",
                "abort-current-script.js",
                "abort-current-script",
            ],
            r#"(function() {
    var target = '{{1}}';
    try {
        var current = document.currentScript;
        if (current && (!target || current.textContent.indexOf(target) !== -1)) {
            throw new ReferenceError('Aborted by native adblock');
        }
    } catch (e) {}
})();"#,
        ),
        make_scriptlet_resource(
            "abort-on-property-read",
            &["abort-on-property-read.js", "aopr.js", "aopr"],
            r#"(function() {
    var prop = '{{1}}';
    try {
        Object.defineProperty(window, prop, {
            get: function() { throw new ReferenceError('Aborted on property read: ' + prop); },
            set: function() {},
            configurable: true
        });
    } catch (e) {}
})();"#,
        ),
        make_scriptlet_resource(
            "abort-on-property-write",
            &["abort-on-property-write.js", "aopw.js", "aopw"],
            r#"(function() {
    var prop = '{{1}}';
    try {
        Object.defineProperty(window, prop, {
            get: function() { return undefined; },
            set: function() { throw new ReferenceError('Aborted on property write: ' + prop); },
            configurable: true
        });
    } catch (e) {}
})();"#,
        ),
        make_scriptlet_resource(
            "prevent-bab",
            &[
                "prevent-bab.js",
                "bab-defuser.js",
                "bab-defuser",
                "nobab.js",
                "nobab",
            ],
            r#"(function() {
    try {
        window.bab = { isReady: true, check: function() {} };
        window.BlockAdBlock = function() { return { on: function() {}, check: function() {} }; };
        window.SniffAdBlock = function() { return { on: function() {}, check: function() {} }; };
        window.FuckAdBlock = function() { return { on: function() {}, check: function() {} }; };
    } catch (e) {}
})();"#,
        ),
        make_scriptlet_resource(
            "json-prune",
            &[
                "json-prune.js",
                "json-prune-fetch-response",
                "json-prune-xhr-response",
            ],
            r#"(function() {
    var propsToPrune = '{{1}}'.split(/ +/);
    function prune(obj) {
        if (!obj || typeof obj !== 'object') return;
        for (var i = 0; i < propsToPrune.length; i++) {
            delete obj[propsToPrune[i]];
        }
    }
    var origParse = JSON.parse;
    JSON.parse = function() {
        var res = origParse.apply(this, arguments);
        prune(res);
        return res;
    };
})();"#,
        ),
        make_scriptlet_resource(
            "prevent-fetch",
            &["prevent-fetch.js"],
            r#"(function() {
    var pattern = '{{1}}';
    var origFetch = window.fetch;
    if (origFetch) {
        window.fetch = function(input, init) {
            var url = typeof input === 'string' ? input : (input && input.url ? input.url : '');
            if (pattern && url.indexOf(pattern) !== -1) {
                return Promise.reject(new TypeError('Blocked fetch'));
            }
            return origFetch.apply(this, arguments);
        };
    }
})();"#,
        ),
        make_scriptlet_resource(
            "prevent-xhr",
            &["prevent-xhr.js"],
            r#"(function() {
    var pattern = '{{1}}';
    var origOpen = XMLHttpRequest.prototype.open;
    XMLHttpRequest.prototype.open = function(method, url) {
        if (pattern && typeof url === 'string' && url.indexOf(pattern) !== -1) {
            this.abort();
            return;
        }
        return origOpen.apply(this, arguments);
    };
})();"#,
        ),
        make_scriptlet_resource(
            "noopfn",
            &["noopfn.js", "noopfunc.js", "noop.js", "noopfunc"],
            r#"(function() {
    var prop = '{{1}}';
    try {
        var parts = prop.split('.');
        var target = window;
        for (var i = 0; i < parts.length - 1; i++) {
            if (!(parts[i] in target)) target[parts[i]] = {};
            target = target[parts[i]];
        }
        target[parts[parts.length - 1]] = function() {};
    } catch (e) {}
})();"#,
        ),
        make_scriptlet_resource(
            "set-local-storage-item",
            &["set-local-storage-item.js"],
            r#"(function() {
    var key = '{{1}}';
    var val = '{{2}}';
    try {
        localStorage.setItem(key, val);
    } catch (e) {}
})();"#,
        ),
        make_scriptlet_resource(
            "set-session-storage-item",
            &["set-session-storage-item.js"],
            r#"(function() {
    var key = '{{1}}';
    var val = '{{2}}';
    try {
        sessionStorage.setItem(key, val);
    } catch (e) {}
})();"#,
        ),
    ]
}

pub fn build_engine(filter_text: String) -> Engine {
    let mut filter_set = FilterSet::new(false);
    filter_set.add_filter_list(filter_text, ParseOptions::default());
    let mut engine = Engine::new_with_filter_set(filter_set);
    engine.use_resources(default_scriptlet_resources());
    engine
}

pub fn load_filter_text(filter_text: String) -> Result<(), String> {
    if filter_text.trim().is_empty() {
        return Err("filter list is empty".to_string());
    }

    let engine = build_engine(filter_text);
    let mut slot = engine_slot()
        .write()
        .map_err(|_| "engine lock is poisoned".to_string())?;
    *slot = Some(engine);
    clear_error();
    Ok(())
}

pub fn load_filter_file(path: &str) -> Result<(), String> {
    let text = fs::read_to_string(path)
        .map_err(|error| format!("failed to read filter list '{path}': {error}"))?;
    load_filter_text(text)
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
pub struct CosmeticResourcesOutput {
    pub hide_selectors: Vec<String>,
    pub injected_script: String,
    pub exceptions: Vec<String>,
}

pub fn get_url_cosmetic_resources(url: &str) -> Result<CosmeticResourcesOutput, String> {
    let slot = engine_slot()
        .read()
        .map_err(|_| "engine lock is poisoned".to_string())?;
    let engine = slot
        .as_ref()
        .ok_or_else(|| "engine is not initialized".to_string())?;

    let cosmetic = engine.url_cosmetic_resources(url);
    let mut hide_selectors: Vec<String> = cosmetic.hide_selectors.into_iter().collect();
    hide_selectors.sort();
    let mut exceptions: Vec<String> = cosmetic.exceptions.into_iter().collect();
    exceptions.sort();

    Ok(CosmeticResourcesOutput {
        hide_selectors,
        injected_script: cosmetic.injected_script,
        exceptions,
    })
}

pub fn serialize_engine() -> Result<Vec<u8>, String> {
    let slot = engine_slot()
        .read()
        .map_err(|_| "engine lock is poisoned".to_string())?;
    let engine = slot
        .as_ref()
        .ok_or_else(|| "engine is not initialized".to_string())?;
    Ok(engine.serialize())
}

pub fn deserialize_engine(data: &[u8]) -> Result<(), String> {
    if data.is_empty() {
        return Err("deserialization data is empty".to_string());
    }
    let mut new_engine = Engine::new_with_filter_set(FilterSet::new(false));
    new_engine.use_resources(default_scriptlet_resources());
    new_engine
        .deserialize(data)
        .map_err(|error| format!("failed to deserialize engine: {error:?}"))?;

    let mut slot = engine_slot()
        .write()
        .map_err(|_| "engine lock is poisoned".to_string())?;
    *slot = Some(new_engine);
    clear_error();
    Ok(())
}

pub fn normalize_request_type(raw: &str) -> &str {
    let trimmed = raw.trim();
    if trimmed.is_empty() {
        return "other";
    }
    match trimmed.to_ascii_lowercase().as_str() {
        "script" | "js" | "javascript" => "script",
        "image" | "img" | "images" | "png" | "jpg" | "jpeg" | "gif" | "webp" => "image",
        "stylesheet" | "css" => "stylesheet",
        "xmlhttprequest" | "xhr" | "ajax" => "xmlhttprequest",
        "subdocument" | "sub_frame" | "frame" | "iframe" => "subdocument",
        "main_frame" | "document" | "doc" => "main_frame",
        "ping" => "ping",
        "beacon" => "beacon",
        "media" | "video" | "audio" => "media",
        "font" | "fonts" | "woff" | "woff2" | "ttf" => "font",
        "websocket" | "ws" | "wss" => "websocket",
        "fetch" => "fetch",
        "csp_report" | "csp-report" => "csp_report",
        "object" | "object_subrequest" => "object",
        "other" => "other",
        _ => "other",
    }
}

pub fn is_ad_stream_or_tracker(url: &str) -> bool {
    let lower = url.to_ascii_lowercase();
    if lower.contains("googleads.g.doubleclick.net")
        || lower.contains("pagead2.googlesyndication.com")
        || lower.contains("youtube.com/api/stats/ads")
        || lower.contains("youtube.com/pagead/")
        || lower.contains("youtube.com/ptracking")
    {
        return true;
    }

    if lower.contains("googlevideo.com/videoplayback")
        && (lower.contains("adformat=") || lower.contains("ad_type=") || lower.contains("ctier=a"))
    {
        return true;
    }

    false
}

pub fn check_request(
    url: &str,
    source_url: &str,
    request_type: &str,
    method: &str,
) -> Result<bool, String> {
    if is_ad_stream_or_tracker(url) {
        return Ok(true);
    }

    let normalized_type = normalize_request_type(request_type);
    let normalized_method = if method.trim().is_empty() {
        "GET"
    } else {
        method.trim()
    };
    let effective_source = if source_url.trim().is_empty() {
        url
    } else {
        source_url.trim()
    };

    let request = Request::new(url, effective_source, normalized_type, normalized_method)
        .map_err(|error| format!("invalid request: {error:?}"))?;
    let slot = engine_slot()
        .read()
        .map_err(|_| "engine lock is poisoned".to_string())?;
    let engine = slot
        .as_ref()
        .ok_or_else(|| "engine is not initialized".to_string())?;
    Ok(engine.check_network_request(&request).should_block())
}

unsafe fn utf8_arg<'a>(pointer: *const u8, length: usize, name: &str) -> Result<&'a str, String> {
    if pointer.is_null() {
        return Err(format!("{name} pointer is null"));
    }
    let bytes = unsafe { slice::from_raw_parts(pointer, length) };
    str::from_utf8(bytes).map_err(|error| format!("{name} is not UTF-8: {error}"))
}

unsafe fn utf16_z_arg(pointer: *const u16, name: &str) -> Result<String, String> {
    if pointer.is_null() {
        return Err(format!("{name} pointer is null"));
    }

    const MAX_UNITS: usize = 32_767;
    let mut length = 0usize;
    while length < MAX_UNITS && unsafe { *pointer.add(length) } != 0 {
        length += 1;
    }
    if length == MAX_UNITS {
        return Err(format!("{name} is not null-terminated"));
    }

    let units = unsafe { slice::from_raw_parts(pointer, length) };
    String::from_utf16(units).map_err(|error| format!("{name} is not UTF-16: {error}"))
}

#[unsafe(no_mangle)]
pub extern "C" fn cna_engine_version() -> *const u8 {
    VERSION.as_ptr()
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn cna_engine_load_filter_text_utf8(
    filter_text: *const u8,
    filter_text_length: usize,
) -> i32 {
    let result = unsafe { utf8_arg(filter_text, filter_text_length, "filter_text") }
        .and_then(|text| load_filter_text(text.to_owned()));
    match result {
        Ok(()) => {
            clear_error();
            CNA_OK
        }
        Err(error) => set_error(error),
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn cna_engine_load_filter_file_utf16(path: *const u16) -> i32 {
    let result =
        unsafe { utf16_z_arg(path, "path") }.and_then(|path_str| load_filter_file(&path_str));
    match result {
        Ok(()) => {
            clear_error();
            CNA_OK
        }
        Err(error) => set_error(error),
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "system" fn cna_remote_initialize(filter_path: *const u16) -> u32 {
    let result =
        unsafe { utf16_z_arg(filter_path, "filter_path") }.and_then(|path| load_filter_file(&path));
    match result {
        Ok(()) => {
            clear_error();
            CNA_OK as u32
        }
        Err(error) => {
            set_error(error);
            u32::MAX
        }
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "system" fn cna_remote_install_network_hook(_unused: *mut ()) -> u32 {
    #[cfg(windows)]
    {
        match unsafe { network_hook::install() } {
            Ok(()) => {
                clear_error();
                CNA_OK as u32
            }
            Err(error) => {
                set_error(error.message);
                error.code
            }
        }
    }

    #[cfg(not(windows))]
    {
        set_error("native Chrome hook is only supported on Windows");
        u32::MAX
    }
}
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cna_log_network_block_utf8(url: *const u8, url_len: usize) -> i32 {
    #[cfg(windows)]
    {
        if let Ok(url_str) = unsafe { utf8_arg(url, url_len, "url") } {
            network_hook::log_network_block(url_str);
            clear_error();
            CNA_OK
        } else {
            set_error("invalid url utf-8")
        }
    }

    #[cfg(not(windows))]
    {
        let _ = (url, url_len);
        CNA_OK
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "system" fn cna_get_hook_resolution_mode(_unused: *mut ()) -> u32 {
    #[cfg(windows)]
    {
        network_hook::get_hook_resolution_mode() as u32
    }
    #[cfg(not(windows))]
    {
        let _ = _unused;
        0
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn cna_get_hook_info(
    out_start_rva: *mut usize,
    out_cancel_rva: *mut usize,
) -> u32 {
    #[cfg(windows)]
    {
        let (start, cancel) = network_hook::get_resolved_rvas();
        if !out_start_rva.is_null() {
            unsafe { *out_start_rva = start };
        }
        if !out_cancel_rva.is_null() {
            unsafe { *out_cancel_rva = cancel };
        }
        network_hook::get_hook_resolution_mode() as u32
    }
    #[cfg(not(windows))]
    {
        let _ = (out_start_rva, out_cancel_rva);
        0
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn cna_scan_chrome_dll_file_utf16(
    path: *const u16,
    out_start_rva: *mut usize,
    out_cancel_rva: *mut usize,
) -> i32 {
    #[cfg(windows)]
    {
        let path_str = match unsafe { utf16_z_arg(path, "path") } {
            Ok(s) => s,
            Err(e) => {
                set_error(e);
                return CNA_ERROR;
            }
        };

        match network_hook::scan_chrome_dll_file(&path_str) {
            Ok((start_rva, cancel_rva)) => {
                if !out_start_rva.is_null() {
                    unsafe { *out_start_rva = start_rva };
                }
                if !out_cancel_rva.is_null() {
                    unsafe { *out_cancel_rva = cancel_rva };
                }
                clear_error();
                CNA_OK
            }
            Err(err) => {
                set_error(err);
                CNA_ERROR
            }
        }
    }
    #[cfg(not(windows))]
    {
        let _ = (path, out_start_rva, out_cancel_rva);
        set_error("pattern scanning chrome.dll is only supported on Windows");
        CNA_ERROR
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn cna_engine_check_utf8(
    url: *const u8,
    url_length: usize,
    source_url: *const u8,
    source_url_length: usize,
    request_type: *const u8,
    request_type_length: usize,
    method: *const u8,
    method_length: usize,
) -> i32 {
    let result = (|| {
        let url = unsafe { utf8_arg(url, url_length, "url") }?;
        let source_url = unsafe { utf8_arg(source_url, source_url_length, "source_url") }?;
        let request_type = unsafe { utf8_arg(request_type, request_type_length, "request_type") }?;
        let method = unsafe { utf8_arg(method, method_length, "method") }?;
        check_request(url, source_url, request_type, method)
    })();

    match result {
        Ok(true) => {
            clear_error();
            CNA_BLOCK
        }
        Ok(false) => {
            clear_error();
            CNA_ALLOW
        }
        Err(error) => set_error(error),
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn cna_engine_url_cosmetic_resources_utf8(
    url: *const u8,
    url_len: usize,
    out_buffer: *mut u8,
    out_capacity: usize,
) -> usize {
    let result = (|| {
        let url_str = unsafe { utf8_arg(url, url_len, "url") }?;
        let resources = get_url_cosmetic_resources(url_str)?;
        serde_json::to_string(&resources).map_err(|e| format!("JSON serialization failed: {e}"))
    })();

    match result {
        Ok(json_str) => {
            let bytes = json_str.as_bytes();
            let len = bytes.len();
            if !out_buffer.is_null() && out_capacity > 0 {
                let copy_len = len.min(out_capacity);
                unsafe {
                    std::ptr::copy_nonoverlapping(bytes.as_ptr(), out_buffer, copy_len);
                    if out_capacity > copy_len {
                        *out_buffer.add(copy_len) = 0;
                    }
                }
            }
            clear_error();
            len
        }
        Err(error) => {
            set_error(error);
            0
        }
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn cna_engine_serialize(out_buffer: *mut u8, out_capacity: usize) -> usize {
    match serialize_engine() {
        Ok(bytes) => {
            let len = bytes.len();
            if !out_buffer.is_null() && out_capacity >= len {
                unsafe {
                    std::ptr::copy_nonoverlapping(bytes.as_ptr(), out_buffer, len);
                }
            }
            clear_error();
            len
        }
        Err(error) => {
            set_error(error);
            0
        }
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn cna_engine_deserialize(data: *const u8, length: usize) -> i32 {
    if data.is_null() || length == 0 {
        return set_error("data pointer is null or length is 0");
    }
    let slice = unsafe { slice::from_raw_parts(data, length) };
    match deserialize_engine(slice) {
        Ok(()) => {
            clear_error();
            CNA_OK
        }
        Err(error) => set_error(error),
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn cna_engine_last_error_utf8(buffer: *mut u8, capacity: usize) -> usize {
    let Ok(slot) = error_slot().read() else {
        return 0;
    };
    let bytes = slot.as_bytes();
    if !buffer.is_null() && capacity > 0 {
        let copy_length = bytes.len().min(capacity.saturating_sub(1));
        unsafe {
            std::ptr::copy_nonoverlapping(bytes.as_ptr(), buffer, copy_length);
            *buffer.add(copy_length) = 0;
        }
    }
    bytes.len()
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Write;
    use std::sync::Mutex;

    static TEST_MUTEX: Mutex<()> = Mutex::new(());

    #[test]
    fn test_network_rules_and_types() {
        let _lock = TEST_MUTEX.lock().unwrap();
        let rules = r#"
||ads.example^$script
||images.example/ad.png$image
||styles.example/ad.css$stylesheet
||api.example/telemetry$xmlhttprequest
||frame.example/ad.html$subdocument
||tracker.example/ping$ping
||video.example/ad.mp4$media
||fonts.example/bad.woff2$font
||ws.example/stream$websocket
||other.example/data$other
||thirdparty.example^$third-party
@@||ads.example/allowed.js$script
@@||images.example/allowed.png$image
"#;
        load_filter_text(rules.to_string()).expect("load filter rules");
        // Script block and exception
        assert!(
            check_request(
                "https://ads.example/banner.js",
                "https://site.example/",
                "script",
                "GET"
            )
            .expect("check")
        );
        assert!(
            !check_request(
                "https://ads.example/allowed.js",
                "https://site.example/",
                "script",
                "GET"
            )
            .expect("check")
        );

        // Image block and exception
        assert!(
            check_request(
                "https://images.example/ad.png",
                "https://site.example/",
                "image",
                "GET"
            )
            .expect("check")
        );
        assert!(
            !check_request(
                "https://images.example/allowed.png",
                "https://site.example/",
                "image",
                "GET"
            )
            .expect("check")
        );

        // Stylesheet
        assert!(
            check_request(
                "https://styles.example/ad.css",
                "https://site.example/",
                "stylesheet",
                "GET"
            )
            .expect("check")
        );
        assert!(
            !check_request(
                "https://styles.example/main.css",
                "https://site.example/",
                "stylesheet",
                "GET"
            )
            .expect("check")
        );

        // XHR / XMLHttpRequest
        assert!(
            check_request(
                "https://api.example/telemetry",
                "https://site.example/",
                "xhr",
                "POST"
            )
            .expect("check")
        );
        assert!(
            check_request(
                "https://api.example/telemetry",
                "https://site.example/",
                "xmlhttprequest",
                "POST"
            )
            .expect("check")
        );

        // Subdocument (iframe)
        assert!(
            check_request(
                "https://frame.example/ad.html",
                "https://site.example/",
                "subdocument",
                "GET"
            )
            .expect("check")
        );
        assert!(
            check_request(
                "https://frame.example/ad.html",
                "https://site.example/",
                "iframe",
                "GET"
            )
            .expect("check")
        );

        // Ping / Beacon
        assert!(
            check_request(
                "https://tracker.example/ping",
                "https://site.example/",
                "ping",
                "POST"
            )
            .expect("check")
        );

        // Media
        assert!(
            check_request(
                "https://video.example/ad.mp4",
                "https://site.example/",
                "media",
                "GET"
            )
            .expect("check")
        );

        // Font
        assert!(
            check_request(
                "https://fonts.example/bad.woff2",
                "https://site.example/",
                "font",
                "GET"
            )
            .expect("check")
        );

        // WebSocket
        assert!(
            check_request(
                "wss://ws.example/stream",
                "https://site.example/",
                "websocket",
                "GET"
            )
            .expect("check")
        );

        // Other
        assert!(
            check_request(
                "https://other.example/data",
                "https://site.example/",
                "other",
                "GET"
            )
            .expect("check")
        );

        // Third-party
        assert!(
            check_request(
                "https://thirdparty.example/banner",
                "https://site.example/",
                "other",
                "GET"
            )
            .expect("check")
        );
        assert!(
            !check_request(
                "https://thirdparty.example/banner",
                "https://thirdparty.example/",
                "other",
                "GET"
            )
            .expect("check")
        );
    }
    #[test]
    fn test_ad_stream_and_tracker_detection() {
        let _lock = TEST_MUTEX.lock().unwrap();
        load_filter_text("||example.com^".to_string()).expect("load filter");

        // DoubleClick & GoogleSyndication
        assert!(
            check_request(
                "https://googleads.g.doubleclick.net/pagead/ads?client=ca-pub-123",
                "https://www.youtube.com/",
                "script",
                "GET"
            )
            .expect("check")
        );
        assert!(
            check_request(
                "https://pagead2.googlesyndication.com/pagead/js/adsbygoogle.js",
                "https://www.youtube.com/",
                "script",
                "GET"
            )
            .expect("check")
        );

        // YouTube ad endpoints
        assert!(
            check_request(
                "https://www.youtube.com/api/stats/ads?v=123&ad_type=1",
                "https://www.youtube.com/",
                "xhr",
                "POST"
            )
            .expect("check")
        );
        assert!(
            check_request(
                "https://www.youtube.com/pagead/parallel?ad_type=video",
                "https://www.youtube.com/",
                "xhr",
                "GET"
            )
            .expect("check")
        );
        assert!(
            check_request(
                "https://www.youtube.com/ptracking?ad=1&v=123",
                "https://www.youtube.com/",
                "ping",
                "GET"
            )
            .expect("check")
        );

        // Video ad streams
        assert!(check_request("https://rr1---sn-abc.googlevideo.com/videoplayback?expire=123&adformat=1_8&sparams=adformat", "https://www.youtube.com/", "media", "GET").expect("check"));
        assert!(check_request("https://rr2---sn-xyz.googlevideo.com/videoplayback?expire=123&ad_type=instream_overlay", "https://www.youtube.com/", "media", "GET").expect("check"));
        assert!(
            check_request(
                "https://rr3---sn-xyz.googlevideo.com/videoplayback?expire=123&ctier=A",
                "https://www.youtube.com/",
                "media",
                "GET"
            )
            .expect("check")
        );

        // Normal video playback stream (should not be blocked by ad stream detector)
        assert!(!check_request("https://rr1---sn-abc.googlevideo.com/videoplayback?expire=123&id=regular_video&itag=18", "https://www.youtube.com/", "media", "GET").expect("check"));
    }

    #[test]
    fn test_cosmetic_rules_extraction() {
        let _lock = TEST_MUTEX.lock().unwrap();
        let rules = r#"
example.com##.ad-banner
example.com##.sponsored-post
example.com#@#.sponsored-post
youtube.com##.ytd-ad-slot-renderer
youtube.com##ytd-in-feed-ad-layout-renderer
youtube.com###player-ads
"#;
        load_filter_text(rules.to_string()).expect("load cosmetic rules");

        let example_res =
            get_url_cosmetic_resources("https://example.com/page").expect("cosmetic resources");
        assert!(
            example_res
                .hide_selectors
                .contains(&".ad-banner".to_string())
        );
        assert!(
            example_res
                .exceptions
                .contains(&".sponsored-post".to_string())
        );

        let yt_res = get_url_cosmetic_resources("https://www.youtube.com/watch?v=test1234")
            .expect("youtube cosmetic");
        assert!(
            yt_res
                .hide_selectors
                .contains(&".ytd-ad-slot-renderer".to_string())
        );
        assert!(
            yt_res
                .hide_selectors
                .contains(&"ytd-in-feed-ad-layout-renderer".to_string())
        );
        assert!(yt_res.hide_selectors.contains(&"#player-ads".to_string()));

        // Other domain should not receive example.com selectors
        let other_res =
            get_url_cosmetic_resources("https://news.example.org/").expect("other cosmetic");
        assert!(!other_res.hide_selectors.contains(&".ad-banner".to_string()));
    }

    #[test]
    fn test_scriptlet_rules() {
        let _lock = TEST_MUTEX.lock().unwrap();
        let rules = r#"
youtube.com##+js(set-constant, ytInitialPlayerResponse.adPlacements, undefined)
youtube.com##+js(set-constant, ytInitialPlayerResponse.playerAds, undefined)
testsite.com##+js(abort-current-inline-script, antiAdblockFunction)
testsite.com##+js(prevent-bab)
"#;
        load_filter_text(rules.to_string()).expect("load scriptlet rules");

        let yt_res = get_url_cosmetic_resources("https://www.youtube.com/watch?v=123")
            .expect("youtube scriptlets");
        assert!(
            yt_res
                .injected_script
                .contains("ytInitialPlayerResponse.adPlacements")
        );
        assert!(
            yt_res
                .injected_script
                .contains("ytInitialPlayerResponse.playerAds")
        );

        let site_res =
            get_url_cosmetic_resources("https://testsite.com/").expect("site scriptlets");
        assert!(site_res.injected_script.contains("antiAdblockFunction"));
        assert!(site_res.injected_script.contains("BlockAdBlock"));
    }

    #[test]
    fn test_engine_serialization_and_deserialization() {
        let _lock = TEST_MUTEX.lock().unwrap();
        let rules = r#"
||blocked-domain.example^$script
example.com##.sidebar-ad
youtube.com##+js(set-constant, ytInitialPlayerResponse.adPlacements, undefined)
"#;
        load_filter_text(rules.to_string()).expect("load rules");

        assert!(
            check_request(
                "https://blocked-domain.example/ad.js",
                "https://site.example/",
                "script",
                "GET"
            )
            .expect("check")
        );

        let serialized = serialize_engine().expect("serialize");
        assert!(!serialized.is_empty());

        // Deserialize into engine
        deserialize_engine(&serialized).expect("deserialize");

        // Verify rules work after deserialization
        assert!(
            check_request(
                "https://blocked-domain.example/ad.js",
                "https://site.example/",
                "script",
                "GET"
            )
            .expect("check after")
        );
        assert!(
            !check_request(
                "https://blocked-domain.example/ad.js",
                "https://site.example/",
                "image",
                "GET"
            )
            .expect("check image")
        );

        let cosmetic = get_url_cosmetic_resources("https://example.com/").expect("cosmetic after");
        assert!(cosmetic.hide_selectors.contains(&".sidebar-ad".to_string()));

        let yt_cosmetic = get_url_cosmetic_resources("https://www.youtube.com/watch?v=abc")
            .expect("yt cosmetic after");
        assert!(
            yt_cosmetic
                .injected_script
                .contains("ytInitialPlayerResponse.adPlacements")
        );
    }

    #[test]
    fn test_c_abi_exports() {
        let _lock = TEST_MUTEX.lock().unwrap();
        let rules = "||cabi-blocked.example^$script\nexample.org##.cabi-ad\n";
        let rules_bytes = rules.as_bytes();

        let ret =
            unsafe { cna_engine_load_filter_text_utf8(rules_bytes.as_ptr(), rules_bytes.len()) };
        assert_eq!(ret, CNA_OK);

        // Version test
        let ver_ptr = cna_engine_version();
        assert!(!ver_ptr.is_null());
        let ver_str = unsafe {
            std::ffi::CStr::from_ptr(ver_ptr.cast())
                .to_str()
                .expect("valid utf-8 version")
        };
        assert!(ver_str.contains("chrome-native-adblock"));

        // Check utf8
        let url = b"https://cabi-blocked.example/banner.js";
        let src = b"https://site.example/";
        let req_type = b"script";
        let method = b"GET";

        let check_res = unsafe {
            cna_engine_check_utf8(
                url.as_ptr(),
                url.len(),
                src.as_ptr(),
                src.len(),
                req_type.as_ptr(),
                req_type.len(),
                method.as_ptr(),
                method.len(),
            )
        };
        assert_eq!(check_res, CNA_BLOCK);

        let allowed_url = b"https://site.example/valid.js";
        let allow_res = unsafe {
            cna_engine_check_utf8(
                allowed_url.as_ptr(),
                allowed_url.len(),
                src.as_ptr(),
                src.len(),
                req_type.as_ptr(),
                req_type.len(),
                method.as_ptr(),
                method.len(),
            )
        };
        assert_eq!(allow_res, CNA_ALLOW);

        // Cosmetic resources utf8
        let page_url = b"https://example.org/home";
        let required_len = unsafe {
            cna_engine_url_cosmetic_resources_utf8(
                page_url.as_ptr(),
                page_url.len(),
                std::ptr::null_mut(),
                0,
            )
        };
        assert!(required_len > 0);

        let mut buf = vec![0u8; required_len + 1];
        let copied = unsafe {
            cna_engine_url_cosmetic_resources_utf8(
                page_url.as_ptr(),
                page_url.len(),
                buf.as_mut_ptr(),
                buf.len(),
            )
        };
        assert_eq!(copied, required_len);

        let json_str = std::str::from_utf8(&buf[..required_len]).expect("valid json utf-8");
        let parsed: CosmeticResourcesOutput =
            serde_json::from_str(json_str).expect("valid cosmetic json");
        assert!(parsed.hide_selectors.contains(&".cabi-ad".to_string()));

        // Serialize C ABI
        let ser_len = unsafe { cna_engine_serialize(std::ptr::null_mut(), 0) };
        assert!(ser_len > 0);

        let mut ser_buf = vec![0u8; ser_len];
        let written_ser = unsafe { cna_engine_serialize(ser_buf.as_mut_ptr(), ser_buf.len()) };
        assert_eq!(written_ser, ser_len);

        // Deserialize C ABI
        let deser_ret = unsafe { cna_engine_deserialize(ser_buf.as_ptr(), ser_buf.len()) };
        assert_eq!(deser_ret, CNA_OK);

        // File loading with UTF-16
        let mut tmp_file = tempfile::NamedTempFile::new().expect("temp file");
        writeln!(tmp_file, "||from-file.example^$script").expect("write temp filter");
        let path_str = tmp_file.path().to_str().expect("path utf-8");
        let utf16_path: Vec<u16> = path_str.encode_utf16().chain(std::iter::once(0)).collect();

        let load_file_res = unsafe { cna_engine_load_filter_file_utf16(utf16_path.as_ptr()) };
        assert_eq!(load_file_res, CNA_OK);

        let file_url = b"https://from-file.example/test.js";
        let file_check = unsafe {
            cna_engine_check_utf8(
                file_url.as_ptr(),
                file_url.len(),
                src.as_ptr(),
                src.len(),
                req_type.as_ptr(),
                req_type.len(),
                method.as_ptr(),
                method.len(),
            )
        };
        assert_eq!(file_check, CNA_BLOCK);

        // Remote initialize compatibility
        let remote_res = unsafe { cna_remote_initialize(utf16_path.as_ptr()) };
        assert_eq!(remote_res, 0);

        // Last error check on error condition
        let invalid_utf16 = [0xD800u16, 0xD800u16, 0u16]; // lone surrogate
        let err_load = unsafe { cna_engine_load_filter_file_utf16(invalid_utf16.as_ptr()) };
        assert_eq!(err_load, CNA_ERROR);

        let mut err_buf = [0u8; 256];
        let err_len = unsafe { cna_engine_last_error_utf8(err_buf.as_mut_ptr(), err_buf.len()) };
        assert!(err_len > 0);
        let err_str = std::str::from_utf8(&err_buf[..err_len]).expect("err utf8");
        assert!(err_str.contains("UTF-16"));

        // Network block log C ABI test
        let test_block_url = b"https://adservice.google.com/adsid/integrator.sync";
        let log_res =
            unsafe { cna_log_network_block_utf8(test_block_url.as_ptr(), test_block_url.len()) };
        assert_eq!(log_res, CNA_OK);

        // Hook resolution query C ABI
        let mode = unsafe { cna_get_hook_resolution_mode(std::ptr::null_mut()) };
        let mut start_rva = 0usize;
        let mut cancel_rva = 0usize;
        let mode_info = unsafe { cna_get_hook_info(&mut start_rva, &mut cancel_rva) };
        assert_eq!(mode, mode_info);
    }
}
