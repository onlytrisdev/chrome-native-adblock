# Third-party notices

Chrome Native Adblock uses or can download content from third-party projects.
Those projects are not affiliated with or endorsed by this repository.

## Compiled dependencies

- `adblock-rust` — Mozilla Public License 2.0
  https://github.com/brave/adblock-rust
- `minhook` Rust bindings and MinHook — licenses are included in their upstream
  distributions and Cargo metadata.
- .NET and Windows App SDK packages — licensed by Microsoft under their
  respective package terms.

The complete Rust dependency versions are recorded in `Cargo.lock`; NuGet
versions are recorded in the project files and generated lock/assets data.

## Filter subscriptions

The application downloads selected subscriptions at runtime and stores them in
the user's local cache. Full downloaded lists are not committed to this
repository or bundled in release archives.

- EasyList, EasyPrivacy, and Fanboy lists:
  https://easylist.to/pages/licence.html
- uBlock Origin uAssets (GPL-3.0):
  https://github.com/uBlockOrigin/uAssets/blob/master/LICENSE
- AdGuard filter lists:
  https://github.com/AdguardTeam/AdguardFilters
- ABPVN:
  https://github.com/abpvn/abpvn
- Other optional regional/security subscriptions are identified by their exact
  source URL in `launcher/ChromeNativeAdblock.Launcher/FilterCatalog.cs`.

The small files under `filters/` are project-maintained offline fallback and
smoke-test rules. They are not snapshots of the full upstream subscriptions.
