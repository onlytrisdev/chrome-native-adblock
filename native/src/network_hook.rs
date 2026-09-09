use minhook::MinHook;
use std::ffi::c_void;
use std::ptr;
use std::slice;
use std::str;
use std::sync::atomic::{AtomicPtr, AtomicU32, AtomicUsize, Ordering};

use crate::check_request;

#[derive(Debug, Clone)]
pub struct InstallError {
    pub code: u32,
    pub message: String,
}

impl InstallError {
    pub fn new(code: u32, message: impl Into<String>) -> Self {
        Self {
            code,
            message: message.into(),
        }
    }
}

type StartFn = unsafe extern "system" fn(*mut c_void);
type CancelWithErrorFn = unsafe extern "system" fn(*mut c_void, i32) -> i32;

const ERR_BLOCKED_BY_CLIENT: i32 = -20;

#[repr(u32)]
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum HookResolutionMode {
    NotInstalled = 0,
    KnownRva = 1,
    DynamicPatternScan = 2,
}

// Build-specific values extracted from Chrome's official PDB.
#[derive(Clone, Copy)]
struct HookRule {
    start_rva: usize,
    cancel_rva: usize,
    url_chain_offset: usize,
    gurl_size: usize,
    start_prefix: &'static [u8],
    cancel_prefix: &'static [u8],
}

const CHROME_152_0_7977_65: HookRule = HookRule {
    start_rva: 0x08BE450,
    cancel_rva: 0x0A5A09C0,
    url_chain_offset: 0x48,
    gurl_size: 0x78,
    start_prefix: &[0x41, 0x56, 0x56, 0x57, 0x53, 0x48, 0x83, 0xEC, 0x58],
    cancel_prefix: &[0x56, 0x57, 0x48, 0x81, 0xEC, 0x98, 0x00, 0x00, 0x00],
};

const CHROME_152_0_7977_76: HookRule = HookRule {
    start_rva: 0x099D910,
    cancel_rva: 0x0A5AFD00,
    url_chain_offset: 0x48,
    gurl_size: 0x78,
    start_prefix: &[0x41, 0x56, 0x56, 0x57, 0x53, 0x48, 0x83, 0xEC, 0x58],
    cancel_prefix: &[0x56, 0x57, 0x48, 0x81, 0xEC, 0x98, 0x00, 0x00, 0x00],
};

const CHROME_153_0_8010_37: HookRule = HookRule {
    start_rva: 0x09256B0,
    cancel_rva: 0x0A85B640,
    url_chain_offset: 0x48,
    gurl_size: 0x78,
    start_prefix: &[0x41, 0x56, 0x56, 0x57, 0x53, 0x48, 0x83, 0xEC, 0x58],
    cancel_prefix: &[0x56, 0x57, 0x48, 0x81, 0xEC, 0x98, 0x00, 0x00, 0x00],
};

const KNOWN_HOOK_RULES: &[HookRule] = &[
    CHROME_152_0_7977_65,
    CHROME_152_0_7977_76,
    CHROME_153_0_8010_37,
];

// Masked wildcard signatures for net::URLRequest::Start and net::URLRequest::CancelWithError
pub const SIG_URL_REQUEST_START: &str = "41 56 56 57 53 48 83 EC ? 48 89 CE 48 8B 05 ? ? ? ? 48 31 E0 48 89 44 24 ? \
     48 8B 81 ? ? 00 00 48 8B 80 ? ? 00 00 83 B8 ? ? 00 00 00 0F 85 ? ? 00 00 \
     83 BE ? ? 00 00 00 0F 85 ? ? 00 00";

pub const SIG_URL_REQUEST_CANCEL_WITH_ERROR: &str = "56 57 48 81 EC ? ? 00 00 48 8B 05 ? ? ? ? 48 31 E0 48 89 84 24 ? ? 00 00 \
     ? ? ? ? ? ? ? ? ? ? ? ? ? ? ? ? ? ? ? ? ? \
     0F 11 47 24 0F 11 47 34 0F 11 47 44";

static ORIGINAL_START: AtomicPtr<c_void> = AtomicPtr::new(ptr::null_mut());
static CANCEL_WITH_ERROR: AtomicPtr<c_void> = AtomicPtr::new(ptr::null_mut());

static HOOK_RESOLUTION_MODE: AtomicU32 = AtomicU32::new(HookResolutionMode::NotInstalled as u32);
static RESOLVED_START_RVA: AtomicUsize = AtomicUsize::new(0);
static RESOLVED_CANCEL_RVA: AtomicUsize = AtomicUsize::new(0);
static RESOLVED_URL_CHAIN_OFFSET: AtomicUsize = AtomicUsize::new(0);
static RESOLVED_GURL_SIZE: AtomicUsize = AtomicUsize::new(0);

pub fn get_hook_resolution_mode() -> HookResolutionMode {
    match HOOK_RESOLUTION_MODE.load(Ordering::Acquire) {
        1 => HookResolutionMode::KnownRva,
        2 => HookResolutionMode::DynamicPatternScan,
        _ => HookResolutionMode::NotInstalled,
    }
}

pub fn get_resolved_rvas() -> (usize, usize) {
    (
        RESOLVED_START_RVA.load(Ordering::Acquire),
        RESOLVED_CANCEL_RVA.load(Ordering::Acquire),
    )
}

// ---------------------------------------------------------------------------
// In-Memory PE Parser
// ---------------------------------------------------------------------------

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PeSection {
    pub name: [u8; 8],
    pub virtual_address: u32,
    pub virtual_size: u32,
    pub raw_data_ptr: u32,
    pub raw_data_size: u32,
    pub characteristics: u32,
}

impl PeSection {
    pub fn name_str(&self) -> &str {
        let len = self.name.iter().position(|&b| b == 0).unwrap_or(8);
        str::from_utf8(&self.name[..len]).unwrap_or(".unknown")
    }

    pub fn is_text(&self) -> bool {
        let name = self.name_str();
        name == ".text" || name.starts_with(".text")
    }
}

#[derive(Debug, Clone)]
#[allow(dead_code)]
pub struct ParsedPe {
    pub image_base: u64,
    pub size_of_image: u32,
    pub entry_point: u32,
    pub sections: Vec<PeSection>,
}

impl ParsedPe {
    pub unsafe fn parse_in_memory(module_base: *const u8) -> Result<Self, &'static str> {
        if module_base.is_null() {
            return Err("null module pointer");
        }

        // Read DOS Header
        let dos_magic = unsafe { ptr::read_unaligned(module_base.cast::<u16>()) };
        if dos_magic != 0x5A4D {
            // "MZ"
            return Err("invalid DOS header magic");
        }

        let e_lfanew = unsafe { ptr::read_unaligned(module_base.add(0x3C).cast::<u32>()) } as usize;
        if e_lfanew > 0x10000 {
            return Err("e_lfanew offset out of reasonable bounds");
        }

        // Read NT Headers (64-bit)
        let nt_headers = unsafe { module_base.add(e_lfanew) };
        let nt_signature = unsafe { ptr::read_unaligned(nt_headers.cast::<u32>()) };
        if nt_signature != 0x00004550 {
            // "PE\0\0"
            return Err("invalid NT headers signature");
        }

        let file_header = unsafe { nt_headers.add(4) };
        let machine = unsafe { ptr::read_unaligned(file_header.cast::<u16>()) };
        if machine != 0x8664 {
            // IMAGE_FILE_MACHINE_AMD64
            return Err("not an AMD64 PE image");
        }

        let num_sections =
            unsafe { ptr::read_unaligned(file_header.add(2).cast::<u16>()) } as usize;
        let opt_header_size =
            unsafe { ptr::read_unaligned(file_header.add(16).cast::<u16>()) } as usize;

        let opt_header = unsafe { nt_headers.add(24) };
        let opt_magic = unsafe { ptr::read_unaligned(opt_header.cast::<u16>()) };
        if opt_magic != 0x20B {
            // PE32+ (64-bit)
            return Err("not a PE32+ 64-bit optional header");
        }

        let entry_point = unsafe { ptr::read_unaligned(opt_header.add(16).cast::<u32>()) };
        let image_base = unsafe { ptr::read_unaligned(opt_header.add(24).cast::<u64>()) };
        let size_of_image = unsafe { ptr::read_unaligned(opt_header.add(56).cast::<u32>()) };

        let section_table_offset = e_lfanew + 24 + opt_header_size;
        let mut sections = Vec::with_capacity(num_sections);

        for i in 0..num_sections {
            let sec_ptr = unsafe { module_base.add(section_table_offset + i * 40) };
            let mut name = [0u8; 8];
            unsafe {
                ptr::copy_nonoverlapping(sec_ptr, name.as_mut_ptr(), 8);
            }
            let virtual_size = unsafe { ptr::read_unaligned(sec_ptr.add(8).cast::<u32>()) };
            let virtual_address = unsafe { ptr::read_unaligned(sec_ptr.add(12).cast::<u32>()) };
            let raw_data_size = unsafe { ptr::read_unaligned(sec_ptr.add(16).cast::<u32>()) };
            let raw_data_ptr = unsafe { ptr::read_unaligned(sec_ptr.add(20).cast::<u32>()) };
            let characteristics = unsafe { ptr::read_unaligned(sec_ptr.add(36).cast::<u32>()) };

            sections.push(PeSection {
                name,
                virtual_address,
                virtual_size,
                raw_data_ptr,
                raw_data_size,
                characteristics,
            });
        }

        Ok(Self {
            image_base,
            size_of_image,
            entry_point,
            sections,
        })
    }

    pub fn parse_file(data: &[u8]) -> Result<Self, &'static str> {
        if data.len() < 64 {
            return Err("file too small for DOS header");
        }
        if &data[0..2] != b"MZ" {
            return Err("invalid DOS header magic");
        }

        let e_lfanew = u32::from_le_bytes(data[0x3C..0x40].try_into().unwrap()) as usize;
        if data.len() < e_lfanew + 264 {
            return Err("file too small for NT headers");
        }

        if &data[e_lfanew..e_lfanew + 4] != b"PE\0\0" {
            return Err("invalid NT headers signature");
        }

        let file_header_offset = e_lfanew + 4;
        let machine = u16::from_le_bytes(
            data[file_header_offset..file_header_offset + 2]
                .try_into()
                .unwrap(),
        );
        if machine != 0x8664 {
            return Err("not an AMD64 PE image");
        }

        let num_sections = u16::from_le_bytes(
            data[file_header_offset + 2..file_header_offset + 4]
                .try_into()
                .unwrap(),
        ) as usize;
        let opt_header_size = u16::from_le_bytes(
            data[file_header_offset + 16..file_header_offset + 18]
                .try_into()
                .unwrap(),
        ) as usize;

        let opt_header_offset = e_lfanew + 24;
        let opt_magic = u16::from_le_bytes(
            data[opt_header_offset..opt_header_offset + 2]
                .try_into()
                .unwrap(),
        );
        if opt_magic != 0x20B {
            return Err("not a PE32+ 64-bit optional header");
        }

        let entry_point = u32::from_le_bytes(
            data[opt_header_offset + 16..opt_header_offset + 20]
                .try_into()
                .unwrap(),
        );
        let image_base = u64::from_le_bytes(
            data[opt_header_offset + 24..opt_header_offset + 32]
                .try_into()
                .unwrap(),
        );
        let size_of_image = u32::from_le_bytes(
            data[opt_header_offset + 56..opt_header_offset + 60]
                .try_into()
                .unwrap(),
        );

        let section_table_offset = e_lfanew + 24 + opt_header_size;
        if data.len() < section_table_offset + num_sections * 40 {
            return Err("file truncated before section headers end");
        }

        let mut sections = Vec::with_capacity(num_sections);
        for i in 0..num_sections {
            let offset = section_table_offset + i * 40;
            let mut name = [0u8; 8];
            name.copy_from_slice(&data[offset..offset + 8]);
            let virtual_size =
                u32::from_le_bytes(data[offset + 8..offset + 12].try_into().unwrap());
            let virtual_address =
                u32::from_le_bytes(data[offset + 12..offset + 16].try_into().unwrap());
            let raw_data_size =
                u32::from_le_bytes(data[offset + 16..offset + 20].try_into().unwrap());
            let raw_data_ptr =
                u32::from_le_bytes(data[offset + 20..offset + 24].try_into().unwrap());
            let characteristics =
                u32::from_le_bytes(data[offset + 36..offset + 40].try_into().unwrap());

            sections.push(PeSection {
                name,
                virtual_address,
                virtual_size,
                raw_data_ptr,
                raw_data_size,
                characteristics,
            });
        }

        Ok(Self {
            image_base,
            size_of_image,
            entry_point,
            sections,
        })
    }

    pub fn text_section(&self) -> Option<&PeSection> {
        self.sections.iter().find(|s| s.is_text())
    }

    pub fn text_slice_file<'a>(&self, file_data: &'a [u8]) -> Option<&'a [u8]> {
        let sec = self.text_section()?;
        let start = sec.raw_data_ptr as usize;
        let size = if sec.virtual_size > 0 {
            (sec.virtual_size as usize).min(sec.raw_data_size as usize)
        } else {
            sec.raw_data_size as usize
        };
        if start + size <= file_data.len() {
            Some(&file_data[start..start + size])
        } else {
            None
        }
    }
}

// ---------------------------------------------------------------------------
// AOB (Array of Bytes / Pattern Mask) Scanner
// ---------------------------------------------------------------------------

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Pattern {
    pub bytes: Vec<u8>,
    pub mask: Vec<u8>, // 0xFF = exact match, 0x00 = wildcard
}

impl Pattern {
    pub fn parse(pattern_str: &str) -> Result<Self, &'static str> {
        let mut bytes = Vec::new();
        let mut mask = Vec::new();

        for token in pattern_str.split_whitespace() {
            let token = token.trim_start_matches("0x").trim_start_matches("0X");
            if token == "?" || token == "??" {
                bytes.push(0);
                mask.push(0x00);
            } else if let Ok(val) = u8::from_str_radix(token, 16) {
                bytes.push(val);
                mask.push(0xFF);
            } else {
                return Err("invalid hex token in pattern");
            }
        }

        if bytes.is_empty() {
            return Err("empty pattern");
        }

        Ok(Self { bytes, mask })
    }
}

pub fn scan_pattern(data: &[u8], pattern: &Pattern) -> Vec<usize> {
    if pattern.bytes.is_empty() || pattern.bytes.len() > data.len() {
        return Vec::new();
    }

    let pat_len = pattern.bytes.len();
    let mask = &pattern.mask;
    let pat_bytes = &pattern.bytes;

    let first_exact = mask.iter().position(|&m| m == 0xFF);
    let (first_idx, first_byte) = match first_exact {
        Some(idx) => (idx, pat_bytes[idx]),
        None => return (0..=data.len() - pat_len).collect(),
    };

    let mut matches = Vec::new();
    let max_search = data.len() - pat_len;
    let mut offset = 0;

    while offset <= max_search {
        let slice_to_search = &data[offset + first_idx..];
        let Some(pos) = slice_to_search.iter().position(|&b| b == first_byte) else {
            break;
        };
        offset += pos;
        if offset > max_search {
            break;
        }

        let candidate = &data[offset..offset + pat_len];
        let mut matched = true;
        for j in 0..pat_len {
            if mask[j] == 0xFF && candidate[j] != pat_bytes[j] {
                matched = false;
                break;
            }
        }
        if matched {
            matches.push(offset);
        }
        offset += 1;
    }

    matches
}

pub fn find_unique_pattern(data: &[u8], pattern: &Pattern) -> Result<usize, &'static str> {
    let matches = scan_pattern(data, pattern);
    match matches.len() {
        0 => Err("pattern not found"),
        1 => Ok(matches[0]),
        _ => Err("multiple pattern matches found; signature is not unique"),
    }
}

pub fn find_cancel_with_error_pattern(
    data: &[u8],
    pattern: &Pattern,
) -> Result<usize, &'static str> {
    let matches = scan_pattern(data, pattern);
    if matches.is_empty() {
        return Err("pattern not found");
    }
    // Filter matches to select URLRequest::CancelWithError(int error_code).
    // URLRequest::Cancel() wrapper explicitly sets 'ba fd ff ff ff 49 89' (mov edx, -3; mov r8, rdi)
    // before calling DoCancel, whereas CancelWithError(int error_code) preserves caller's edx.
    for &offset in &matches {
        let inspect_len = (data.len() - offset).min(120);
        let slice = &data[offset..offset + inspect_len];
        let is_cancel_wrapper = slice.windows(7).any(|w| {
            w[0] == 0xBA
                && w[1] == 0xFD
                && w[2] == 0xFF
                && w[3] == 0xFF
                && w[4] == 0xFF
                && w[5] == 0x49
                && w[6] == 0x89
        });
        if !is_cancel_wrapper {
            return Ok(offset);
        }
    }
    Ok(matches[0])
}

// ---------------------------------------------------------------------------
// Instruction Prologue & Safety Checks
// ---------------------------------------------------------------------------

pub fn is_valid_prologue_start(bytes: &[u8]) -> bool {
    if bytes.len() < 5 {
        return false;
    }
    // Disallow already hooked bytes (E9 rel32, FF 25 rip+0), breakpoints (CC), ret (C3)
    if bytes[0] == 0xCC
        || bytes[0] == 0xC3
        || bytes[0] == 0xE9
        || (bytes[0] == 0xFF && bytes[1] == 0x25)
    {
        return false;
    }
    // URLRequest::Start starts with push r14 (41 56) or push rsi (56) or sub rsp (48 83 ec / 48 81 ec)
    // Accept standard x64 prologues
    (bytes[0] == 0x41 && (bytes[1] & 0xF8) == 0x50) // push r8-r15
        || ((bytes[0] & 0xF8) == 0x50)              // push rax-rdi
        || (bytes[0] == 0x48 && bytes[1] == 0x83 && bytes[2] == 0xEC) // sub rsp, imm8
        || (bytes[0] == 0x48 && bytes[1] == 0x81 && bytes[2] == 0xEC) // sub rsp, imm32
        || (bytes[0] == 0x48 && bytes[1] == 0x89 && (bytes[2] & 0xC0) == 0x40) // mov [rsp+xx], reg
}

pub fn is_valid_prologue_cancel(bytes: &[u8]) -> bool {
    if bytes.len() < 5 {
        return false;
    }
    if bytes[0] == 0xCC
        || bytes[0] == 0xC3
        || bytes[0] == 0xE9
        || (bytes[0] == 0xFF && bytes[1] == 0x25)
    {
        return false;
    }
    // URLRequest::CancelWithError starts with push rsi (56) or push rdi (57) or sub rsp (48 81 ec / 48 83 ec)
    ((bytes[0] & 0xF8) == 0x50)
        || (bytes[0] == 0x48 && bytes[1] == 0x81 && bytes[2] == 0xEC)
        || (bytes[0] == 0x48 && bytes[1] == 0x83 && bytes[2] == 0xEC)
}

// ---------------------------------------------------------------------------
// Win32 External Functions & Logging
// ---------------------------------------------------------------------------

unsafe extern "system" {
    fn GetModuleHandleW(module_name: *const u16) -> *mut c_void;
    fn OutputDebugStringW(lpOutputString: *const u16);
    fn CreateFileW(
        lpFileName: *const u16,
        dwDesiredAccess: u32,
        dwShareMode: u32,
        lpSecurityAttributes: *mut c_void,
        dwCreationDisposition: u32,
        dwFlagsAndAttributes: u32,
        hTemplateFile: *mut c_void,
    ) -> *mut c_void;
    fn WriteFile(
        hFile: *mut c_void,
        lpBuffer: *const c_void,
        nNumberOfBytesToWrite: u32,
        lpNumberOfBytesWritten: *mut u32,
        lpOverlapped: *mut c_void,
    ) -> i32;
    fn FlushFileBuffers(hFile: *mut c_void) -> i32;
    fn CloseHandle(hObject: *mut c_void) -> i32;
}

const LOG_PIPE_NAME: &[u16] = &[
    b'\\' as u16,
    b'\\' as u16,
    b'.' as u16,
    b'\\' as u16,
    b'p' as u16,
    b'i' as u16,
    b'p' as u16,
    b'e' as u16,
    b'\\' as u16,
    b'C' as u16,
    b'h' as u16,
    b'r' as u16,
    b'o' as u16,
    b'm' as u16,
    b'e' as u16,
    b'N' as u16,
    b'a' as u16,
    b't' as u16,
    b'i' as u16,
    b'v' as u16,
    b'e' as u16,
    b'A' as u16,
    b'd' as u16,
    b'b' as u16,
    b'l' as u16,
    b'o' as u16,
    b'c' as u16,
    b'k' as u16,
    b'_' as u16,
    b'L' as u16,
    b'o' as u16,
    b'g' as u16,
    0,
];

pub fn log_hook_event(message: &str) {
    let mut debug_buf = [0u16; 2048];
    let mut idx = 0;
    for ch in message.encode_utf16() {
        if idx >= debug_buf.len() - 1 {
            break;
        }
        debug_buf[idx] = ch;
        idx += 1;
    }
    debug_buf[idx] = 0;
    unsafe {
        OutputDebugStringW(debug_buf.as_ptr());
    }

    let handle = unsafe {
        CreateFileW(
            LOG_PIPE_NAME.as_ptr(),
            0x40000000, // GENERIC_WRITE
            0,
            ptr::null_mut(),
            3,    // OPEN_EXISTING
            0x80, // FILE_ATTRIBUTE_NORMAL
            ptr::null_mut(),
        )
    };

    if !handle.is_null() && handle != (usize::MAX as *mut c_void) {
        let mut pipe_buf = [0u8; 4096];
        let msg_bytes = message.as_bytes();
        let len = msg_bytes.len().min(pipe_buf.len() - 2);
        pipe_buf[..len].copy_from_slice(&msg_bytes[..len]);
        pipe_buf[len] = b'\n';

        let mut written = 0u32;
        unsafe {
            WriteFile(
                handle,
                pipe_buf.as_ptr().cast(),
                (len + 1) as u32,
                &mut written,
                ptr::null_mut(),
            );
            FlushFileBuffers(handle);
            CloseHandle(handle);
        }
    }
}

pub fn log_network_block(url: &str) {
    // 1. OutputDebugStringW with [CNA-NET-BLOCK] <url>
    let mut debug_buf = [0u16; 2048];
    const PREFIX: &[u16] = &[
        b'[' as u16,
        b'C' as u16,
        b'N' as u16,
        b'A' as u16,
        b'-' as u16,
        b'N' as u16,
        b'E' as u16,
        b'T' as u16,
        b'-' as u16,
        b'B' as u16,
        b'L' as u16,
        b'O' as u16,
        b'C' as u16,
        b'K' as u16,
        b']' as u16,
        b' ' as u16,
    ];
    let prefix_len = PREFIX.len();
    debug_buf[..prefix_len].copy_from_slice(PREFIX);
    let mut idx = prefix_len;
    for ch in url.encode_utf16() {
        if idx >= debug_buf.len() - 1 {
            break;
        }
        debug_buf[idx] = ch;
        idx += 1;
    }
    debug_buf[idx] = 0;
    unsafe {
        OutputDebugStringW(debug_buf.as_ptr());
    }

    // 2. Named pipe write to \\.\pipe\ChromeNativeAdblock_Log
    let handle = unsafe {
        CreateFileW(
            LOG_PIPE_NAME.as_ptr(),
            0x40000000, // GENERIC_WRITE
            0,
            ptr::null_mut(),
            3,    // OPEN_EXISTING
            0x80, // FILE_ATTRIBUTE_NORMAL
            ptr::null_mut(),
        )
    };

    if !handle.is_null() && handle != (usize::MAX as *mut c_void) {
        let mut pipe_buf = [0u8; 4096];
        const MSG_PREFIX: &[u8] = b"NET_BLOCK|";
        let msg_prefix_len = MSG_PREFIX.len();
        pipe_buf[..msg_prefix_len].copy_from_slice(MSG_PREFIX);
        let mut p_idx = msg_prefix_len;
        let url_bytes = url.as_bytes();
        let max_url_bytes = (pipe_buf.len() - msg_prefix_len - 1).min(url_bytes.len());
        pipe_buf[p_idx..p_idx + max_url_bytes].copy_from_slice(&url_bytes[..max_url_bytes]);
        p_idx += max_url_bytes;
        pipe_buf[p_idx] = b'\n';
        p_idx += 1;

        let mut written = 0u32;
        unsafe {
            WriteFile(
                handle,
                pipe_buf.as_ptr().cast(),
                p_idx as u32,
                &mut written,
                ptr::null_mut(),
            );
            FlushFileBuffers(handle);
            CloseHandle(handle);
        }
    }
}

unsafe fn has_prefix(address: *const u8, prefix: &[u8]) -> bool {
    !address.is_null() && unsafe { slice::from_raw_parts(address, prefix.len()) } == prefix
}

unsafe fn request_url<'a>(request: *const u8) -> Option<&'a str> {
    if request.is_null() {
        return None;
    }

    let url_chain_offset = RESOLVED_URL_CHAIN_OFFSET.load(Ordering::Acquire);
    let gurl_size = RESOLVED_GURL_SIZE.load(Ordering::Acquire);
    if gurl_size == 0 {
        return None;
    }

    let vector = unsafe { request.add(url_chain_offset) };
    let begin = unsafe { ptr::read_unaligned(vector.cast::<*const u8>()) };
    let end_or_count = unsafe { ptr::read_unaligned(vector.add(8).cast::<usize>()) };
    if begin.is_null() || end_or_count == 0 {
        return None;
    }

    // Support both vector layouts:
    // 1. Pointer pair: { begin, end, cap } where end > begin as usize (Chrome 152 and earlier)
    // 2. Count based: { begin, count, cap } where count <= 64 (Chrome 153+)
    let gurl = if end_or_count > begin as usize {
        let byte_length = end_or_count.checked_sub(begin as usize)?;
        if byte_length % gurl_size != 0 || byte_length / gurl_size > 64 {
            return None;
        }
        unsafe { (end_or_count as *const u8).sub(gurl_size) }
    } else if end_or_count <= 64 {
        let count = end_or_count;
        unsafe { begin.add((count - 1) * gurl_size) }
    } else {
        return None;
    };

    // Chromium uses libc++'s 24-byte alternate string layout. Short
    // strings store data inline and a 7-bit size in byte 23; long strings use
    // {data*, size, 63-bit capacity} with the top capacity bit set.
    let tag = unsafe { *gurl.add(23) };
    let (data, length) = if tag & 0x80 == 0 {
        (gurl, usize::from(tag & 0x7f))
    } else {
        (
            unsafe { ptr::read_unaligned(gurl.cast::<*const u8>()) },
            unsafe { ptr::read_unaligned(gurl.add(8).cast::<usize>()) },
        )
    };
    if data.is_null() || length == 0 || length > 2 * 1024 * 1024 {
        return None;
    }
    str::from_utf8(unsafe { slice::from_raw_parts(data, length) }).ok()
}

unsafe extern "system" fn start_detour(request: *mut c_void) {
    let url_opt = unsafe { request_url(request.cast::<u8>()) };
    let should_block = url_opt
        .map(|url| check_request(url, url, "other", "GET").unwrap_or(false))
        .unwrap_or(false);

    let original = ORIGINAL_START.load(Ordering::Acquire);
    if !original.is_null() {
        let original: StartFn = unsafe { std::mem::transmute(original) };
        unsafe { original(request) };
    }

    if should_block {
        if let Some(url) = url_opt {
            log_network_block(url);
        }
        let cancel = CANCEL_WITH_ERROR.load(Ordering::Acquire);
        if !cancel.is_null() {
            let cancel: CancelWithErrorFn = unsafe { std::mem::transmute(cancel) };
            let _ = unsafe { cancel(request, ERR_BLOCKED_BY_CLIENT) };
        }
    }
}

// ---------------------------------------------------------------------------
// Fail-closed build-specific installation logic
// ---------------------------------------------------------------------------

pub unsafe fn install() -> Result<(), InstallError> {
    if !ORIGINAL_START.load(Ordering::Acquire).is_null() {
        return Ok(());
    }

    let module_name: Vec<u16> = "chrome.dll\0".encode_utf16().collect();
    let module = unsafe { GetModuleHandleW(module_name.as_ptr()) }.cast::<u8>();
    if module.is_null() {
        return Err(InstallError::new(
            2,
            "chrome.dll is not loaded in this process",
        ));
    }

    let parsed_pe = unsafe { ParsedPe::parse_in_memory(module) }.map_err(|err| {
        InstallError::new(3, format!("Failed to parse chrome.dll PE headers: {err}"))
    })?;

    let text_sec = parsed_pe
        .text_section()
        .ok_or_else(|| InstallError::new(3, "Failed to locate .text section in chrome.dll"))?;

    let text_start = text_sec.virtual_address as usize;
    let text_end = text_start + (text_sec.virtual_size as usize);
    let rule = KNOWN_HOOK_RULES
        .iter()
        .find(|rule| {
            if rule.start_rva < text_start
                || rule.start_rva + rule.start_prefix.len() > text_end
                || rule.cancel_rva < text_start
                || rule.cancel_rva + rule.cancel_prefix.len() > text_end
            {
                return false;
            }

            let start = unsafe { module.add(rule.start_rva) };
            let cancel = unsafe { module.add(rule.cancel_rva) };
            unsafe {
                has_prefix(start, rule.start_prefix) && has_prefix(cancel, rule.cancel_prefix)
            }
        })
        .ok_or_else(|| {
            InstallError::new(
                3,
                "Unsupported chrome.dll build: no verified hook rule matched. Refusing dynamic installation",
            )
        })?;

    RESOLVED_URL_CHAIN_OFFSET.store(rule.url_chain_offset, Ordering::Release);
    RESOLVED_GURL_SIZE.store(rule.gurl_size, Ordering::Release);
    let start_rva = rule.start_rva;
    let cancel_rva = rule.cancel_rva;
    let resolution_mode = HookResolutionMode::KnownRva;

    let start_ptr = unsafe { module.add(start_rva) };
    let cancel_ptr = unsafe { module.add(cancel_rva) };

    // 3. Verify instruction prologue safety
    if start_rva < text_start
        || start_rva + 16 > text_end
        || cancel_rva < text_start
        || cancel_rva + 16 > text_end
    {
        return Err(InstallError::new(
            3,
            "Resolved hook addresses reside outside .text section boundaries",
        ));
    }

    let start_prologue = unsafe { slice::from_raw_parts(start_ptr, 16) };
    let cancel_prologue = unsafe { slice::from_raw_parts(cancel_ptr, 16) };

    if !is_valid_prologue_start(start_prologue) {
        return Err(InstallError::new(
            3,
            format!("Invalid or unsafe function prologue for URLRequest::Start at 0x{start_rva:X}"),
        ));
    }
    if !is_valid_prologue_cancel(cancel_prologue) {
        return Err(InstallError::new(
            3,
            format!(
                "Invalid or unsafe function prologue for URLRequest::CancelWithError at 0x{cancel_rva:X}"
            ),
        ));
    }

    // 4. Install MinHook detours
    CANCEL_WITH_ERROR.store(cancel_ptr.cast(), Ordering::Release);
    let trampoline = unsafe { MinHook::create_hook(start_ptr.cast(), start_detour as *mut c_void) }
        .map_err(|status| InstallError::new(4, format!("MinHook create failed: {status}")))?;
    ORIGINAL_START.store(trampoline, Ordering::Release);

    if let Err(status) = unsafe { MinHook::enable_hook(start_ptr.cast()) } {
        ORIGINAL_START.store(ptr::null_mut(), Ordering::Release);
        CANCEL_WITH_ERROR.store(ptr::null_mut(), Ordering::Release);
        let _ = unsafe { MinHook::remove_hook(start_ptr.cast()) };
        return Err(InstallError::new(
            5,
            format!("MinHook enable failed: {status}"),
        ));
    }

    HOOK_RESOLUTION_MODE.store(resolution_mode as u32, Ordering::Release);
    RESOLVED_START_RVA.store(start_rva, Ordering::Release);
    RESOLVED_CANCEL_RVA.store(cancel_rva, Ordering::Release);

    // 5. Log resolution status
    match resolution_mode {
        HookResolutionMode::KnownRva => {
            let msg = format!(
                "[CNA-HOOK] Fast-path RVA match for Chrome (Start=0x{start_rva:X}, Cancel=0x{cancel_rva:X})."
            );
            log_hook_event(&msg);
        }
        HookResolutionMode::DynamicPatternScan => {
            let msg = format!(
                "[CNA-HOOK] Dynamically resolved URLRequest::Start at 0x{start_rva:X} and CancelWithError at 0x{cancel_rva:X} via pattern scan."
            );
            log_hook_event(&msg);
        }
        _ => {}
    }

    Ok(())
}

// ---------------------------------------------------------------------------
// Offline Scanning Helpers (for testing & offline tooling)
// ---------------------------------------------------------------------------

pub fn scan_chrome_dll_bytes(data: &[u8]) -> Result<(usize, usize), String> {
    let parsed_pe = ParsedPe::parse_file(data).map_err(|e| e.to_string())?;
    let text_sec = parsed_pe
        .text_section()
        .ok_or_else(|| "Failed to locate .text section in PE".to_string())?;
    let text_slice = parsed_pe
        .text_slice_file(data)
        .ok_or_else(|| "Failed to slice .text section data".to_string())?;

    let start_pattern = Pattern::parse(SIG_URL_REQUEST_START).map_err(|e| e.to_string())?;
    let cancel_pattern =
        Pattern::parse(SIG_URL_REQUEST_CANCEL_WITH_ERROR).map_err(|e| e.to_string())?;

    let start_offset =
        find_unique_pattern(text_slice, &start_pattern).map_err(|e| e.to_string())?;
    let cancel_offset =
        find_cancel_with_error_pattern(text_slice, &cancel_pattern).map_err(|e| e.to_string())?;
    let start_rva = (text_sec.virtual_address as usize) + start_offset;
    let cancel_rva = (text_sec.virtual_address as usize) + cancel_offset;

    Ok((start_rva, cancel_rva))
}

pub fn scan_chrome_dll_file(path: &str) -> Result<(usize, usize), String> {
    let data = std::fs::read(path).map_err(|e| format!("Failed to read '{path}': {e}"))?;
    scan_chrome_dll_bytes(&data)
}

// ---------------------------------------------------------------------------
// Comprehensive Unit Tests
// ---------------------------------------------------------------------------

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_pattern_parse_valid() {
        let pat = Pattern::parse("41 56 ? 53 48 83 EC ? 48 ?? CE").unwrap();
        assert_eq!(
            pat.bytes,
            vec![
                0x41, 0x56, 0x00, 0x53, 0x48, 0x83, 0xEC, 0x00, 0x48, 0x00, 0xCE
            ]
        );
        assert_eq!(
            pat.mask,
            vec![
                0xFF, 0xFF, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0xFF, 0x00, 0xFF
            ]
        );
    }

    #[test]
    fn test_pattern_parse_invalid() {
        assert!(Pattern::parse("").is_err());
        assert!(Pattern::parse("   ").is_err());
        assert!(Pattern::parse("41 ZZ 56").is_err());
        assert!(Pattern::parse("41 1234 56").is_err());
    }

    #[test]
    fn test_scan_pattern_exact_and_wildcards() {
        let data = [
            0x90, 0x90, 0x41, 0x56, 0x56, 0x57, 0x53, 0x48, 0x83, 0xEC, 0x58, 0x48, 0x89, 0xCE,
            0xCC, 0x90, 0x41, 0x56, 0x56, 0x57, 0x53, 0x48, 0x83, 0xEC, 0x78, 0x48, 0x89, 0xCE,
            0xCC,
        ];
        let pat = Pattern::parse("41 56 56 57 53 48 83 EC ? 48 89 CE").unwrap();
        let matches = scan_pattern(&data, &pat);
        assert_eq!(matches, vec![2, 16]);

        let unique = find_unique_pattern(&data[..15], &pat).unwrap();
        assert_eq!(unique, 2);

        let err = find_unique_pattern(&data, &pat);
        assert!(err.is_err());
    }

    #[test]
    fn test_scan_pattern_no_match() {
        let data = [0x90; 100];
        let pat = Pattern::parse("41 56 56").unwrap();
        assert_eq!(scan_pattern(&data, &pat), Vec::<usize>::new());
        assert!(find_unique_pattern(&data, &pat).is_err());
    }

    #[test]
    fn test_prologue_safety_checks() {
        // Valid prologues
        assert!(is_valid_prologue_start(&[
            0x41, 0x56, 0x56, 0x57, 0x53, 0x48, 0x83, 0xEC, 0x58
        ]));
        assert!(is_valid_prologue_start(&[
            0x56, 0x57, 0x53, 0x48, 0x83, 0xEC, 0x20
        ]));
        assert!(is_valid_prologue_cancel(&[
            0x56, 0x57, 0x48, 0x81, 0xEC, 0x98, 0x00, 0x00, 0x00
        ]));

        // Invalid prologues (breakpoint, already hooked, ret, short)
        assert!(!is_valid_prologue_start(&[0xCC, 0x90, 0x90, 0x90, 0x90]));
        assert!(!is_valid_prologue_start(&[0xC3, 0x90, 0x90, 0x90, 0x90]));
        assert!(!is_valid_prologue_start(&[0xE9, 0x10, 0x20, 0x30, 0x40]));
        assert!(!is_valid_prologue_start(&[
            0xFF, 0x25, 0x00, 0x00, 0x00, 0x00
        ]));
        assert!(!is_valid_prologue_start(&[0x56]));
    }

    fn build_synthetic_pe(text_content: &[u8]) -> Vec<u8> {
        let mut pe = vec![0u8; 0x1000]; // 4KB header page
        // DOS header
        pe[0] = b'M';
        pe[1] = b'Z';
        pe[0x3C..0x40].copy_from_slice(&0x80u32.to_le_bytes()); // e_lfanew = 0x80

        let nt = 0x80;
        pe[nt..nt + 4].copy_from_slice(b"PE\0\0");
        // FileHeader
        let fh = nt + 4;
        pe[fh..fh + 2].copy_from_slice(&0x8664u16.to_le_bytes()); // Machine AMD64
        pe[fh + 2..fh + 4].copy_from_slice(&1u16.to_le_bytes()); // NumberOfSections = 1
        pe[fh + 16..fh + 18].copy_from_slice(&0xF0u16.to_le_bytes()); // SizeOfOptionalHeader = 240

        // OptionalHeader
        let opt = nt + 24;
        pe[opt..opt + 2].copy_from_slice(&0x20Bu16.to_le_bytes()); // Magic PE32+
        pe[opt + 16..opt + 20].copy_from_slice(&0x1000u32.to_le_bytes()); // EntryPoint = 0x1000
        pe[opt + 24..opt + 32].copy_from_slice(&0x180000000u64.to_le_bytes()); // ImageBase
        pe[opt + 56..opt + 60].copy_from_slice(&0x20000u32.to_le_bytes()); // SizeOfImage

        // Section table at nt + 24 + 240 = 0x80 + 264 = 0x188
        let sec = nt + 24 + 240;
        pe[sec..sec + 8].copy_from_slice(b".text\0\0\0");
        let v_size = text_content.len() as u32;
        let r_size = ((text_content.len() + 0x1FF) / 0x200 * 0x200) as u32;
        pe[sec + 8..sec + 12].copy_from_slice(&v_size.to_le_bytes()); // VirtualSize
        pe[sec + 12..sec + 16].copy_from_slice(&0x1000u32.to_le_bytes()); // VirtualAddress = 0x1000
        pe[sec + 16..sec + 20].copy_from_slice(&r_size.to_le_bytes()); // SizeOfRawData
        pe[sec + 20..sec + 24].copy_from_slice(&0x1000u32.to_le_bytes()); // PointerToRawData = 0x1000
        pe[sec + 36..sec + 40].copy_from_slice(&0x60000020u32.to_le_bytes()); // Characteristics

        // Append section raw data
        pe.resize(0x1000 + (r_size as usize), 0xCC);
        pe[0x1000..0x1000 + text_content.len()].copy_from_slice(text_content);

        pe
    }

    #[test]
    fn test_pe_parser_synthetic() {
        let pe_data = build_synthetic_pe(&[0x90; 128]);
        let parsed = ParsedPe::parse_file(&pe_data).unwrap();
        assert_eq!(parsed.image_base, 0x180000000);
        assert_eq!(parsed.sections.len(), 1);
        let sec = parsed.text_section().unwrap();
        assert_eq!(sec.name_str(), ".text");
        assert_eq!(sec.virtual_address, 0x1000);
        assert_eq!(sec.virtual_size, 128);

        let slice = parsed.text_slice_file(&pe_data).unwrap();
        assert_eq!(slice.len(), 128);
        assert_eq!(slice[0], 0x90);
    }

    #[test]
    fn test_dynamic_rva_resolution_synthetic() {
        // Construct realistic bytecode containing Start and CancelWithError
        let mut text = vec![0x90; 0x2000];

        // Place URLRequest::Start at offset 0x100 (RVA 0x1100)
        let start_bytes = [
            0x41, 0x56, 0x56, 0x57, 0x53, 0x48, 0x83, 0xEC, 0x58, 0x48, 0x89, 0xCE, 0x48, 0x8B,
            0x05, 0xAA, 0xBB, 0xCC, 0x10, 0x48, 0x31, 0xE0, 0x48, 0x89, 0x44, 0x24, 0x50, 0x48,
            0x8B, 0x81, 0xB8, 0x00, 0x00, 0x00, 0x48, 0x8B, 0x80, 0x18, 0x01, 0x00, 0x00, 0x83,
            0xB8, 0xD0, 0x00, 0x00, 0x00, 0x00, 0x0F, 0x85, 0x9F, 0x01, 0x00, 0x00, 0x83, 0xBE,
            0xC8, 0x02, 0x00, 0x00, 0x00, 0x0F, 0x85, 0x56, 0x01, 0x00, 0x00,
        ];
        text[0x100..0x100 + start_bytes.len()].copy_from_slice(&start_bytes);

        // Place URLRequest::CancelWithError at offset 0x500 (RVA 0x1500)
        let cancel_bytes = [
            0x56, 0x57, 0x48, 0x81, 0xEC, 0x98, 0x00, 0x00, 0x00, 0x48, 0x8B, 0x05, 0x70, 0x16,
            0xBD, 0x06, 0x48, 0x31, 0xE0, 0x48, 0x89, 0x84, 0x24, 0x90, 0x00, 0x00, 0x00, 0x31,
            0xC0, 0x48, 0x8D, 0x7C, 0x24, 0x20, 0x66, 0x89, 0x47, 0x20, 0x0F, 0x57, 0xC0, 0x0F,
            0x29, 0x47, 0x10, 0x0F, 0x29, 0x07, 0x0F, 0x11, 0x47, 0x24, 0x0F, 0x11, 0x47, 0x34,
            0x0F, 0x11, 0x47, 0x44, 0x49, 0xB8, 0x00, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00,
        ];
        text[0x500..0x500 + cancel_bytes.len()].copy_from_slice(&cancel_bytes);

        let pe_data = build_synthetic_pe(&text);
        let (start_rva, cancel_rva) = scan_chrome_dll_bytes(&pe_data).unwrap();

        assert_eq!(start_rva, 0x1000 + 0x100);
        assert_eq!(cancel_rva, 0x1000 + 0x500);
    }

    #[test]
    fn test_scan_real_chrome_dll_if_present() {
        let possible_paths = [
            (
                r"C:\Program Files\Google\Chrome\Application\153.0.8010.37\chrome.dll",
                0x09256B0,
                0x0A85B640,
            ),
            (
                r"C:\Program Files\Google\Chrome\Application\152.0.7977.76\chrome.dll",
                0x099D910,
                0x0A5AFD00,
            ),
            (
                r"C:\Program Files\Google\Chrome\Application\152.0.7977.65\chrome.dll",
                0x08BE450,
                0x0A5A09C0,
            ),
            (
                r"C:\Program Files (x86)\Google\Chrome\Application\152.0.7977.65\chrome.dll",
                0x08BE450,
                0x0A5A09C0,
            ),
        ];

        for (path, exp_start, exp_cancel) in possible_paths {
            if std::path::Path::new(path).exists() {
                let (start_rva, cancel_rva) =
                    scan_chrome_dll_file(path).expect("Failed to scan real chrome.dll");
                assert_eq!(start_rva, exp_start);
                assert_eq!(cancel_rva, exp_cancel);
                break;
            }
        }
    }

    #[test]
    fn test_dynamic_pattern_scan_bypassing_fast_path() {
        let possible_paths = [
            r"C:\Program Files\Google\Chrome\Application\152.0.7977.65\chrome.dll",
            r"C:\Program Files (x86)\Google\Chrome\Application\152.0.7977.65\chrome.dll",
        ];

        for path in possible_paths {
            if std::path::Path::new(path).exists() {
                let data = std::fs::read(path).expect("Failed to read chrome.dll");
                let parsed_pe = ParsedPe::parse_file(&data).expect("Failed to parse chrome.dll PE");
                let text_sec = parsed_pe
                    .text_section()
                    .expect("Failed to locate .text section in chrome.dll");
                let text_slice = parsed_pe
                    .text_slice_file(&data)
                    .expect("Failed to get .text section slice");

                // Explicitly perform dynamic pattern scan (bypassing fast path prefix check)
                let start_pattern = Pattern::parse(SIG_URL_REQUEST_START).unwrap();
                let cancel_pattern = Pattern::parse(SIG_URL_REQUEST_CANCEL_WITH_ERROR).unwrap();

                let start_offset = find_unique_pattern(text_slice, &start_pattern)
                    .expect("Dynamic scan for Start failed");
                let cancel_offset = find_cancel_with_error_pattern(text_slice, &cancel_pattern)
                    .expect("Dynamic scan for CancelWithError failed");
                let scanned_start_rva = (text_sec.virtual_address as usize) + start_offset;
                let scanned_cancel_rva = (text_sec.virtual_address as usize) + cancel_offset;

                assert_eq!(scanned_start_rva, CHROME_152_0_7977_65.start_rva);
                assert_eq!(scanned_cancel_rva, CHROME_152_0_7977_65.cancel_rva);
                assert_eq!(scanned_start_rva, 0x08BE450);
                assert_eq!(scanned_cancel_rva, 0x0A5A09C0);
                break;
            }
        }
    }

    #[test]
    fn test_scan_multiple_chromium_builds_if_present() {
        let builds = [
            (
                r"C:\Program Files\Google\Chrome\Application\153.0.8010.37\chrome.dll",
                0x09256B0,
                0x0A85B640,
            ),
            (
                r"C:\Program Files\Google\Chrome\Application\152.0.7977.76\chrome.dll",
                0x099D910,
                0x0A5AFD00,
            ),
            (
                r"C:\Program Files\Google\Chrome\Application\152.0.7977.65\chrome.dll",
                0x08BE450,
                0x0A5A09C0,
            ),
            (
                r"C:\Program Files\CocCoc\Browser\Application\151.0.7922.176\browser.dll",
                0x08BE1B0,
                0x0AAF6950,
            ),
            (
                r"C:\Program Files\BraveSoftware\Brave-Browser\Application\152.1.94.117\chrome.dll",
                0x0992CA0,
                0x0B98EEC0,
            ),
            (
                r"C:\Users\ADMIN\AppData\Local\ms-playwright\chromium-1223\chrome-win64\chrome.dll",
                0x09BE170,
                0x09D53BD0,
            ),
            (
                r"C:\Users\ADMIN\.cache\puppeteer\chrome\win64-148.0.7778.97\chrome-win64\chrome.dll",
                0x09BE170,
                0x09D53B40,
            ),
        ];

        for (path, expected_start, expected_cancel) in builds {
            if std::path::Path::new(path).exists() {
                let (start_rva, cancel_rva) = scan_chrome_dll_file(path)
                    .unwrap_or_else(|e| panic!("Failed to scan '{path}': {e}"));
                assert_eq!(start_rva, expected_start, "Start RVA mismatch for '{path}'");
                assert_eq!(
                    cancel_rva, expected_cancel,
                    "Cancel RVA mismatch for '{path}'"
                );
            }
        }
    }

    #[test]
    fn test_request_url_decoding_pointer_and_count_layouts() {
        RESOLVED_URL_CHAIN_OFFSET.store(0x48, Ordering::Release);
        RESOLVED_GURL_SIZE.store(0x78, Ordering::Release);

        // Construct synthetic URL: "http://example.com/ad" (21 chars, short string)
        let url_str = "http://example.com/ad";
        let mut gurl_buffer = vec![0u8; 0x78];
        gurl_buffer[..url_str.len()].copy_from_slice(url_str.as_bytes());
        gurl_buffer[23] = url_str.len() as u8; // short string tag and length

        let begin_ptr = gurl_buffer.as_ptr();

        // 1. Test Pointer Layout (Chrome 152 style: begin, end > begin, cap)
        let mut req_ptr_layout = vec![0u8; 0x100];
        let end_ptr = unsafe { begin_ptr.add(0x78) };
        unsafe {
            let vec_ptr = req_ptr_layout.as_mut_ptr().add(0x48);
            std::ptr::write_unaligned(vec_ptr.cast::<*const u8>(), begin_ptr);
            std::ptr::write_unaligned(vec_ptr.add(8).cast::<*const u8>(), end_ptr);
            std::ptr::write_unaligned(vec_ptr.add(16).cast::<*const u8>(), end_ptr);
        }
        let parsed_ptr = unsafe { request_url(req_ptr_layout.as_ptr()) };
        assert_eq!(parsed_ptr, Some(url_str));

        // 2. Test Count Layout (Chrome 153 style: begin, count <= 64, cap)
        let mut req_count_layout = vec![0u8; 0x100];
        unsafe {
            let vec_ptr = req_count_layout.as_mut_ptr().add(0x48);
            std::ptr::write_unaligned(vec_ptr.cast::<*const u8>(), begin_ptr);
            std::ptr::write_unaligned(vec_ptr.add(8).cast::<usize>(), 1usize);
            std::ptr::write_unaligned(vec_ptr.add(16).cast::<usize>(), 1usize);
        }
        let parsed_count = unsafe { request_url(req_count_layout.as_ptr()) };
        assert_eq!(parsed_count, Some(url_str));
    }
}
