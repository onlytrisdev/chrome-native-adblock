# Security Policy

## Supported versions

Only the latest GitHub release is supported. Native
network hooking is supported only for Chrome builds explicitly listed in the
README and in `native/hook_rules`.

## Reporting a vulnerability

Please do not open a public issue for a vulnerability that could cause arbitrary
code execution, unsafe memory access, attachment to the wrong browser process,
or compromise of the filter update path.

Use GitHub's private vulnerability reporting page:

https://github.com/onlytrisdev/chrome-native-adblock/security/advisories/new

Include the Chrome version, Windows version, reproduction steps, logs with
personal URLs removed, and whether the issue reproduces with the managed Chrome
profile. Please do not include secrets, cookies, or complete browser profiles.

## Security invariants

- Unknown Chrome layouts must fail closed before installing a native hook.
- The signed Chrome installation on disk must never be patched.
- CDP must be associated with the profile created for the current launch.
- A failed filter update must preserve the last-known-good cache.
