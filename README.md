# Chrome Native Adblock v1.0.0 (Production Stable Release)

High-performance 100% RAM-only native ad filtering and Manifest V2 enabler for
stock Google Chrome on Windows.

Chrome Native Adblock injects a native adblock engine directly into Chrome's
Network Service process in memory, validates build-specific PE rules, and provides
a modern Windows 11 WinUI 3 desktop dashboard.

## What it does

- Evaluates EasyList/ABP-style network and cosmetic rules through
  [`adblock-rust`](https://github.com/brave/adblock-rust).
- Injects the network engine into Chrome's Network Service without modifying
  the signed `chrome.dll` file.
- Generates cosmetic/scriptlet payloads from the exact URL of each tab. Rules
  for YouTube or Vietnamese news sites are not injected into unrelated sites.
- Downloads filter subscriptions with a 64 MiB limit, validates that they
  contain rules, and atomically replaces the last-known-good cache.
- Uses an ephemeral loopback DevTools port tied to the dedicated
  `%LOCALAPPDATA%\ChromeNativeAdblock\Profile` profile.
- Can optionally enable Manifest V2 using the semantic RAM patch from
  [chrome-enable-mv2](https://github.com/onlytrisdev/chrome-enable-mv2).

No Chrome sandbox or code-integrity mitigation is disabled. No telemetry is
collected by this project.

## Compatibility

The native network hook supports **Chrome 152.0.7977.65 x64** and
**Chrome 152.0.7977.76 x64**. Function RVAs, prologues, and `URLRequest`/`GURL`
layouts are recorded per build under `native/hook_rules/` and verified before
installation.

Every other Chrome layout is rejected before a hook is installed. Do not copy
RVAs or object offsets between Chrome builds.

The current low-level hook sees the final URL but not complete request metadata,
so it evaluates requests as resource type `other`, method `GET`, with the request
URL as its source URL. Generic blocking and exception rules work; type-only or
initiator-dependent rules such as `$script`, `$image`, `$xhr`, or `$third-party`
are not yet fully represented by the native hook.

YouTube blocking is best-effort and can change when YouTube changes its player.
Subscription scriptlets are deliberately not executed in YouTube's page context
because response-pruning scriptlets can trigger its anti-adblock dialog; network
rules, cosmetic filtering, visible skip controls, and bounded short-ad acceleration
remain active.
The batch smoke test reports `INCONCLUSIVE` when the player, playback progress,
or seek evidence cannot be verified; it does not count those cases as passes.

## Download

Download `ChromeNativeAdblock-GUI-v1.0.1-win-x64.zip` from
[Releases](https://github.com/onlytrisdev/chrome-native-adblock/releases), extract
the archive, and run `ChromeNativeAdblock.Gui.exe`.

The GUI and CLI use a separate Chrome profile by default. Existing bookmarks,
cookies, and extensions from the normal Chrome profile are intentionally not
copied.

## Build and test

Requirements: Windows x64, Rust stable, .NET 10 SDK, and the Windows App SDK
workload required by the WinUI project.

```powershell
cargo test --workspace
cargo build --workspace --release
dotnet test .\ChromeNativeAdblock.slnx -c Release --filter "Category!=Integration"
dotnet build .\ChromeNativeAdblock.slnx -c Release
```

Chrome-dependent validation on a machine with the supported Chrome build:

```powershell
dotnet run --project .\launcher\ChromeNativeAdblock.Launcher -c Release -- analyze-mv2
dotnet run --project .\launcher\ChromeNativeAdblock.Launcher -c Release -- engine-smoke
dotnet run --project .\launcher\ChromeNativeAdblock.Launcher -c Release -- cosmetic-smoke
dotnet run --project .\launcher\ChromeNativeAdblock.Launcher -c Release -- network-smoke
```

Create release archives:

```powershell
.\scripts\package-release.ps1 -Version v1.0.0
```

## Security model

- Unknown hook layouts fail closed.
- Hook targets must reside in executable `.text` and match verified prefixes.
- `chrome.dll` and other Chrome installation files are never changed on disk.
- CDP discovery trusts only a fresh `DevToolsActivePort` from the managed
  profile; it does not fall back to another Chrome profile or port 9222.
- Filter downloads are HTTPS subscription URLs controlled by their respective
  maintainers. See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

Please read [SECURITY.md](SECURITY.md) before reporting an injection, parser, or
update-channel vulnerability.

## License

Source code is licensed under the Mozilla Public License 2.0. See [LICENSE](LICENSE).
