#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
duration_seconds="${1:-10}"
output_directory="${2:-$repository_root/artifacts/fuzz-smoke}"
fuzz_project="$repository_root/tests/Skopka.Chat.FuzzTests/Skopka.Chat.FuzzTests.csproj"
corpus_directory="$repository_root/tests/Skopka.Chat.FuzzTests/corpus"
binary_directory="$output_directory/bin"
findings_directory="$output_directory/findings"

if [[ ! "$duration_seconds" =~ ^[1-9][0-9]*$ ]]; then
  echo "Fuzz duration must be a positive whole number of seconds." >&2
  exit 2
fi

if ! command -v afl-fuzz >/dev/null 2>&1; then
  echo "afl-fuzz is required. Install the AFL++ package before running this script." >&2
  exit 2
fi

if [[ -e "$output_directory" ]]; then
  echo "Fuzz output already exists: $output_directory" >&2
  exit 2
fi

mkdir -p "$binary_directory"

dotnet build "$fuzz_project" \
  --configuration Release \
  --no-restore \
  --output "$binary_directory"

fuzz_assembly="$binary_directory/Skopka.Chat.FuzzTests.dll"
protocol_assembly="$binary_directory/Skopka.Chat.Protocol.dll"
transport_assembly="$binary_directory/Skopka.Chat.Transport.Http.dll"
client_assembly="$binary_directory/Skopka.Chat.Client.dll"

dotnet "$fuzz_assembly" --replay "$corpus_directory"
dotnet tool restore
# Keep the harness uninstrumented: its entry point runs before SharpFuzz initializes AFL shared memory.
dotnet tool run sharpfuzz -- "$protocol_assembly"
dotnet tool run sharpfuzz -- "$transport_assembly"
dotnet tool run sharpfuzz -- "$client_assembly"

export AFL_I_DONT_CARE_ABOUT_MISSING_CRASHES=1
export AFL_NO_AFFINITY=1
export AFL_NO_UI=1
export AFL_SKIP_CPUFREQ=1
export AFL_SKIP_BIN_CHECK=1

afl-fuzz \
  -i "$corpus_directory" \
  -o "$findings_directory" \
  -V "$duration_seconds" \
  -t 2000 \
  -m none \
  -- dotnet "$fuzz_assembly"

crash_count="$(find "$findings_directory" -type f -path '*/crashes/id:*' | wc -l)"
if [[ "$crash_count" -ne 0 ]]; then
  echo "Fuzz smoke found $crash_count crashing input(s) in $findings_directory." >&2
  exit 1
fi

echo "Fuzz smoke completed without crashes. Findings: $findings_directory"
