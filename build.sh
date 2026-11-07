#!/usr/bin/env bash
# Сборка БазоНавигатора в dist/<rid>[-light]/ без установки. Нужен .NET SDK 10.
#   ./build.sh                    автономный вариант для этой системы (.NET внутри)
#   ./build.sh --light            лёгкий вариант (нужен установленный .NET 10 Runtime)
#   ./build.sh --runtime win-x64  другая платформа: linux-x64, linux-arm64, win-x64
#   ./build.sh --test             перед сборкой запустить тесты
set -euo pipefail
cd "$(dirname "$0")"

light=false; test=false; runtime=""
while [ $# -gt 0 ]; do
    case "$1" in
        --light) light=true ;;
        --test) test=true ;;
        --runtime) shift; runtime="${1:-}"; [ -n "$runtime" ] || { echo "Укажите платформу после --runtime." >&2; exit 2; } ;;
        -h|--help) sed -n '2,6p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) echo "Неизвестный параметр: $1 (см. --help)" >&2; exit 2 ;;
    esac
    shift
done
if [ -z "$runtime" ]; then
    case "$(uname -m)" in
        aarch64|arm64) runtime=linux-arm64 ;;
        *) runtime=linux-x64 ;;
    esac
fi
# Only known runtime identifiers: the name becomes part of the folder that is deleted below.
case "$runtime" in
    win-x64|win-x86|win-arm64|linux-x64|linux-arm64|linux-musl-x64|linux-musl-arm64|osx-x64|osx-arm64) ;;
    *) echo "Неизвестная платформа: $runtime (например win-x64, linux-x64, linux-arm64)." >&2; exit 2 ;;
esac
command -v dotnet >/dev/null || { echo "Не найден dotnet: установите .NET SDK 10." >&2; exit 1; }

if $test; then dotnet test src/StartManager.Tests -c Release --nologo; fi

output="dist/$runtime$($light && echo -light || true)"
# Only a real dist folder inside the project is cleaned: a link could point anywhere.
mkdir -p dist
if [ -L dist ] || [ -L "$output" ] || [ "$(cd dist && pwd -P)" != "$(pwd -P)/dist" ]; then
    echo "dist или $output — ссылка на другой каталог; очистка отменена." >&2; exit 2
fi
rm -rf "$output"
# SDK 10.0.1xx (Linux VM) reads "--self-contained false" as --self-contained and drops "false": the flag forms only.
self_contained=$($light && echo --no-self-contained || echo --self-contained)
dotnet publish src/StartManager/StartManager.csproj -c Release -r "$runtime" "$self_contained" \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false \
    -o "$output" --nologo
find "$output" -name '*.pdb' -delete
case "$runtime" in win-*) ;; *) rm -f "$output/Start-with-log.cmd" ;; esac
echo "Готово: $output$($light && echo ' (нужен .NET 10 Runtime)' || true)"
