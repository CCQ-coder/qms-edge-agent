#!/bin/sh
set -eu

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
temp_dir=$(mktemp -d)
server_pid=
cleanup() {
    result=$?
    if [ "$result" -ne 0 ] && [ -f "$temp_dir/server.log" ]; then cat "$temp_dir/server.log"; fi
    if [ -n "$server_pid" ]; then kill "$server_pid" 2>/dev/null || true; fi
    rm -rf "$temp_dir"
}
trap cleanup EXIT INT TERM

mcs -nologo -sdk:4 -target:exe -r:System.Web.Extensions.dll \
    -out:"$temp_dir/HosonMesMockCli.exe" "$script_dir/HosonMesMockCli.cs" "$script_dir/HosonMesServer.cs" "$script_dir/MesConfig.cs"
mcs -nologo -sdk:4 -target:winexe -main:HosonMesMock -r:System.Web.Extensions.dll -r:System.Windows.Forms.dll -r:System.Drawing.dll \
    -out:"$temp_dir/HosonMesMock.exe" "$script_dir/HosonMesMock.cs" "$script_dir/HosonMesMockCli.cs" "$script_dir/HosonMesServer.cs" "$script_dir/MesConfig.cs"
mcs -nologo -sdk:4 -target:exe -r:System.Web.Extensions.dll \
    -out:"$temp_dir/ServerTests.exe" "$script_dir/ServerTests.cs" "$script_dir/HosonMesServer.cs" "$script_dir/MesConfig.cs"
mono "$temp_dir/ServerTests.exe"
cp "$script_dir/mes_server_config.json" "$temp_dir/mes_server_config.json"
mono "$temp_dir/HosonMesMock.exe" 127.0.0.1 16681 >"$temp_dir/server.log" 2>&1 &
server_pid=$!

attempt=0
until curl -s -o /dev/null -m 1 http://127.0.0.1:16681/api/mes/invoke; do
    attempt=$((attempt + 1))
    if [ "$attempt" -ge 30 ]; then cat "$temp_dir/server.log"; exit 1; fi
    sleep 0.1
done

status=$(curl -sS -m 5 -o "$temp_dir/login.json" -w '%{http_code}' \
    -H 'Content-Type: application/json' -d '{"id":"REQ-1","method":"login","data":{"device_code":"TEST-01"}}' \
    http://127.0.0.1:16681/api/mes/invoke)
test "$status" = 200
grep -q 'hosonsoftusr1' "$temp_dir/login.json"

status=$(curl -sS -m 5 -o "$temp_dir/job.json" -w '%{http_code}' \
    -H 'Content-Type: application/json' -d '{"id":"REQ-2","method":"get_job_name_by_scan","data":{"barcode":"TEST"}}' \
    http://127.0.0.1:16681/api/mes/invoke)
test "$status" = 200
grep -q 'job_name' "$temp_dir/job.json"

status=$(curl -sS -m 5 -o "$temp_dir/scan.json" -w '%{http_code}' \
    -H 'Content-Type: application/json' -d '{"id":"REQ-3","method":"scan_result","data":{}}' \
    http://127.0.0.1:16681/api/mes/invoke)
test "$status" = 200
grep -Eq '"success"[[:space:]]*:[[:space:]]*false' "$temp_dir/scan.json"

status=$(curl -sS -m 5 -o "$temp_dir/unknown.json" -w '%{http_code}' \
    -H 'Content-Type: application/json' -d '{"method":"not_configured"}' \
    http://127.0.0.1:16681/api/mes/invoke)
test "$status" = 404
grep -q 'Unknown method' "$temp_dir/unknown.json"

status=$(curl -sS -m 5 -o "$temp_dir/invalid.json" -w '%{http_code}' \
    -H 'Content-Type: application/json' -d '{broken' \
    http://127.0.0.1:16681/api/mes/invoke)
test "$status" = 400

status=$(curl -sS -m 5 -o "$temp_dir/path.json" -w '%{http_code}' \
    -H 'Content-Type: application/json' -d '{"method":"login"}' \
    http://127.0.0.1:16681/wrong-path)
test "$status" = 404

grep -q 'login' "$temp_dir/server.log"
grep -q 'get_job_name_by_scan' "$temp_dir/server.log"
echo 'Hoson MES mock integration checks passed.'
