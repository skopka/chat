#!/usr/bin/env bash
set -euo pipefail

# Native Apple tool failures do not always produce GitHub annotations. Preserve
# their bounded diagnostic tail in the check result as well as the normal log.
run_gate() {
  local phase="$1"
  shift
  local log_path
  log_path="$(mktemp)"
  if dotnet "$@" 2>&1 | tee "$log_path"; then
    rm -f "$log_path"
    return 0
  fi

  local diagnostic
  diagnostic="$(tail -n 80 "$log_path" | tail -c 12000)"
  rm -f "$log_path"
  diagnostic="${diagnostic//'%'/'%25'}"
  diagnostic="${diagnostic//$'\r'/'%0D'}"
  diagnostic="${diagnostic//$'\n'/'%0A'}"
  echo "::error title=MAUI ${phase} failed::${diagnostic}"
  return 1
}

sample="samples/Skopka.Chat.Maui.Sample/Skopka.Chat.Maui.Sample.csproj"
run_gate "iOS build" build "$sample" --framework net10.0-ios --configuration Release -p:RuntimeIdentifier=iossimulator-x64
run_gate "Mac Catalyst build" build "$sample" --framework net10.0-maccatalyst --configuration Release -p:RuntimeIdentifier=maccatalyst-x64
run_gate "Mac Catalyst trimming" publish "$sample" --framework net10.0-maccatalyst --configuration Release --no-restore -p:RuntimeIdentifier=maccatalyst-x64 -p:PublishTrimmed=true -p:RunAOTCompilation=false
