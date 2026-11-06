#!/usr/bin/env bash
set -euo pipefail

log_path="$(mktemp)"
trap 'rm -f "$log_path"' EXIT
if dotnet workload restore samples/Skopka.Chat.Maui.Sample/Skopka.Chat.Maui.Sample.csproj --version 10.0.101 2>&1 | tee "$log_path"; then
  exit 0
fi
diagnostic="$(tail -n 80 "$log_path" | tail -c 12000)"
diagnostic="${diagnostic//%/%25}"
diagnostic="${diagnostic//$'\r'/%0D}"
diagnostic="${diagnostic//$'\n'/%0A}"
echo "::error title=MAUI workload restore failed::${diagnostic}"
exit 1
