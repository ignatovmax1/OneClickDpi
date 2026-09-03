# OneClick DPI

Windows 10/11 prototype of a one-button hybrid DPI and selective tunnel client.
It uses a typed strategy catalog, never executes BAT files, validates bundled
engine binaries, and remembers the best profile per network. Discord remains
direct. When direct YouTube or Telegram checks fail, an official Tor Expert
Bundle creates a Snowflake SOCKS tunnel and a localhost HTTP CONNECT proxy.
Blocked YouTube control domains and Telegram use Tor. ChatGPT and Claude use a
separate official Psiphon tunnel-core route with an automatically selected
supported exit. YouTube
video CDN, thumbnails, Discord, and unrelated traffic remain on the fast direct
DPI path.

## Build

```powershell
dotnet build OneClickDpi.slnx -c Release
dotnet run --project tests/OneClickDpi.Core.Tests -c Release
dotnet publish src/OneClickDpi.App -c Release -r win-x64 --self-contained true
```

Starting with version 0.6.1, the published GUI contains its runtime components
as embedded resources and safely extracts or repairs them under the user's local
application-data directory. `OneClickDpi.exe` can therefore be moved and run by
itself. Windows will ask for administrator permission because WinDivert requires
elevated privileges.

## Current scope

This is the first vertical slice, not a finished public release. It includes:

- WPF GUI with a central connect/disconnect button;
- four Flowseal 1.10.1-compatible strategy families without BAT files;
- protocol-aware probes for Discord Gateway and Telegram MTProto;
- selective routing through 127.0.0.1:19081: YouTube control domains and Telegram through Tor Snowflake, ChatGPT and Claude through Psiphon with automatic NL/DE/FI failover;
- bounded parallel tunnel startup, so a failed transport cannot leave the GUI connecting forever;
- one-time prefilled Telegram SOCKS5 setup through its official deep link;
- direct DPI delivery for `googlevideo.com`, `ytimg.com`, and `ggpht.com` media;
- signed and encrypted background updates with no account or login UI;
- transactional backup and restoration of Windows system proxy settings;
- automatic candidate scoring and per-network cache;
- strict SHA-256 validation of the bundled v72.9 engine components;
- process ownership: the app only stops the process it started.

Still required before public distribution:

- split the elevated engine controller into a hardened Windows service;
- add authenticated Discord voice media validation without reading user tokens;
- sign the application and installer;
- add independent signed strategy-list updates;
- field-test against multiple ISPs and network types.

Tor is a separate GPLv3 executable distributed as an unmodified component. Its
license notices are bundled under `Tunnel/Licenses`. Tor exits can be slower and
some websites may challenge or rate-limit shared exit addresses.

Psiphon tunnel-core is a separate GPLv3 executable from the official Psiphon
repository. It runs only as a local SOCKS/HTTP proxy for the AI route and never
changes the Windows system proxy itself. Its license is bundled under
`Tunnel/Licenses`.

The updater reads a separate release-only channel. That channel contains no
source code and publishes only an ECDSA-signed manifest plus an AES-256-GCM
encrypted package. The application checks it anonymously; users never sign in
to GitHub.
