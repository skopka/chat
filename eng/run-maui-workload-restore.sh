#!/usr/bin/env bash
set -euo pipefail

python3 - <<'PY'
import json
from pathlib import Path
p = Path("global.json")
data = json.loads(p.read_text(encoding="utf-8"))
data["sdk"]["workloadVersion"] = "10.0.101"
p.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")
PY

log_path="$(mktemp)"
trap 'rm -f "$log_path"' EXIT
if { dotnet workload restore samples/Skopka.Chat.Maui.Sample/Skopka.Chat.Maui.Sample.csproj && test "$(dotnet workload --version)" = "10.0.101" && dotnet workload --info; } 2>&1 | tee "$log_path"; then
  exit 0
fi
diagnostic="$(tail -n 80 "$log_path" | tail -c 12000)"
diagnostic="${diagnostic//%/%25}"
diagnostic="${diagnostic//$'\r'/%0D}"
diagnostic="${diagnostic//$'\n'/%0A}"
echo "::error title=MAUI workload restore failed::${diagnostic}"
exit 1
