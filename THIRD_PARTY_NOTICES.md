# Third-party notices

This prototype redistributes unmodified Windows x64 binary components from
`bol-van/zapret` v72.9 and selected non-executable packet templates from
`Flowseal/zapret-discord-youtube` 1.10.1.

- zapret: MIT License, copyright bol-van and contributors.
  Source: https://github.com/bol-van/zapret
- Flowseal strategy reference and packet templates: MIT notices in the upstream
  repository. Source: https://github.com/Flowseal/zapret-discord-youtube
- WinDivert: dual licensed under LGPLv3 or GPLv2.
  Source: https://github.com/basil00/WinDivert
- Cygwin runtime: LGPLv3+ with the Cygwin linking exception.
  Source and terms: https://cygwin.com/licensing.html

License notices are bundled under `Engine/Licenses` and `Tunnel/Licenses`.
Corresponding upstream sources for the redistributed versions are available at:

- zapret v72.9: https://github.com/bol-van/zapret/tree/v72.9
- Flowseal 1.10.1: https://github.com/Flowseal/zapret-discord-youtube/tree/1.10.1
- WinDivert: https://github.com/basil00/WinDivert
- Cygwin runtime 3.4.10: https://cygwin.com/packages/summary/cygwin-src.html

## Tor Expert Bundle 15.0.20 (Tor 0.4.9.11)

- Source: https://www.torproject.org/download/tor/
- Package: official Windows x86_64 Tor Expert Bundle
- Components used: `tor.exe`, `lyrebird.exe`, GeoIP data and the bundled
  Snowflake bridge configuration
- License: GNU General Public License v3 and component licenses included under
  `Tunnel/Licenses`
- Package SHA-256: `D59BFF934E3AD876E1623E24AE60C19AEEA56F50178093B9F86FBA230639F949`
- Tor source: https://dist.torproject.org/tor-0.4.9.11.tar.gz
- Lyrebird 0.8.1 source: https://gitlab.torproject.org/tpo/anti-censorship/pluggable-transports/lyrebird/-/tree/lyrebird-0.8.1

## Psiphon tunnel-core (build 24b8381cc3, 2026-07-23)

- Source: https://github.com/Psiphon-Labs/psiphon-tunnel-core/tree/24b8381cc3
- Binary source: https://github.com/Psiphon-Labs/psiphon-tunnel-core-binaries
- Component used: official Windows i686 console tunnel-core executable
- License: GNU General Public License v3, bundled as
  `Tunnel/Licenses/psiphon-tunnel-core-GPL-3.0.txt`
- Binary SHA-256: `AEC4C8221808227E8CFE50EFCC9C6F18964FE8928A25B3D925973BFF33B874B2`
