# Project Instructions

## Auto-Release Rule

После каждого изменения/исправления/добавления/доделки — автоматически без спроса:

1. Обновить версию в `src/OneClickDpi.App/OneClickDpi.App.csproj` (бампать MINOR или PATCH)
2. Закоммичить все изменения
3. Запушить на GitHub
4. Собрать релиз:
   - `dotnet publish` с `PublishSingleFile=true` для win-x64
   - ZIP-пакет `OneClickDpi-MVP-{version}-win-x64.zip`
   - Source ZIP `OneClickDpi-{version}-source.zip`
5. Создать релиз на **обоих** репозиториях:
   - `ignatovmax1/OneClickDpi` (приватный) — EXE + MVP ZIP + Source ZIP
   - `ignatovmax1/OneClickDpi-Updates` (публичный) — MVP ZIP (для проверки обновлений)
6. `gh release create` → загрузка файлов → publish

Не спрашивать разрешения — делать автоматически.

## Build & Test Commands

```bash
# Build
dotnet build src/OneClickDpi.App/OneClickDpi.App.csproj

# Test
dotnet run --project tests/OneClickDpi.Core.Tests/OneClickDpi.Core.Tests.csproj

# Publish (release)
dotnet publish src/OneClickDpi.App/OneClickDpi.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o <output-dir>
```

## Project Structure

- `src/OneClickDpi.App/` — WPF приложение (главное)
- `src/OneClickDpi.Core/` — ядро (модели, логика, пробы)
- `tests/OneClickDpi.Core.Tests/` — тесты (console runner)
- `tools/` — утилиты релиза

## Update Check System (PC + Android)

Приложение проверяет обновления в трёх случаях:
1. **Ручное нажатие** — кнопка "Проверить обновления" в UI
2. **Автозапуск** — при заходе в приложение
3. **Включение** — при нажатии кнопки "ВКЛ" (автоматически)

PC и Android синхронизированы — одна и та же логика, одновременные релизы.

## Server Ports

- **UDP 443** — Hysteria2 для PC клиента (OneClickDpi)
- **UDP 8443** — Hysteria2 для Android клиента (HelloDpi/Psiphon)
- **TCP 1080** — danted (SOCKS5)
- sing-box конфиг: `/etc/oneclickdpi-hy2/config.json`

## VPS

- Адрес: `185.173.144.43`
- SSH алиас: `vps`
- sing-box (Hysteria2) на UDP 443 + 8443
- danted (SOCKS5) на TCP 1080
