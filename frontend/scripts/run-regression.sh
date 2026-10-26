#!/usr/bin/env bash
# A–H 全量回归（M5）。
#
# 为什么不是「一个后端 + 一串脚本」：这些 verify 脚本大半会自建挂载、自建媒体库、
# 自清数据，彼此的前提互相破坏（`verify-media` 要求「全部」页签下只有它那一个库，
# `verify-media-dimensions` 会自己再挂一个 /e2e 到别的目录）。所以每轮都从
# **独立数据库 + 独立后端进程 + 独立装置** 起步 —— 慢一点，但失败信息永远指向真因，
# 而不是「上一轮留下的脏数据」。
#
# 用法：
#   bash frontend/scripts/run-regression.sh                 # 全部
#   bash frontend/scripts/run-regression.sh ops media       # 只跑指定轮
#
# 前置：`dotnet build`、`pnpm -C frontend build`、`.e2e/venv`（openpyxl/python-docx/Pillow）、
#      ffmpeg 在 PATH 里。
set -uo pipefail

# Windows 上 `pwd` 给的是 /f/... 形式，而 node / dotnet / python 拿到的是原生路径；
# 混着用会出现 `F:\f\Code\...` 这种拼接错误的路径。统一取 `pwd -W`（拿不到再退回 `pwd`）。
abspath() { ( cd "$1" && { pwd -W 2>/dev/null || pwd; } ); }
REPO="$(abspath "$(dirname "${BASH_SOURCE[0]}")/../..")"
FE="$REPO/frontend"
PORT="${PORT:-5055}"
BASE="http://127.0.0.1:$PORT"
PASS="e2e12345"
# BASE 从环境变量取，统一导出，避免 `VAR=x func` 的作用域歧义。
# MSYS_NO_PATHCONV 不能全局导出：它会让 `tasklist //FI ...` 的 //FI 不再转成 /FI，
# 端口占用检测直接失效。只在跑 node 脚本时用 `env` 前缀单独给。
export BASE
SERVER="$REPO/src/OpenList.Server/bin/Debug/net10.0/OpenList.Server.dll"
RUNS="$REPO/.e2e/runs"
GEN="$FE/scripts/fixtures/gen_fixtures.py"
SETUP="$FE/scripts/fixtures/setup.mjs"

PY="$REPO/.e2e/venv/Scripts/python.exe"
[ -x "$PY" ] || PY="python"

mkdir -p "$RUNS"
SUMMARY=()
FAILED=0
SERVER_PID=""

say() { printf '\n\033[1m== %s\033[0m\n' "$*"; }

# 上一轮异常退出时可能留下一个还占着端口的服务（它绑着旧数据库），
# 那样新一轮的创建挂载会写进另一个库，症状是「接口 200 但列表为空」。
# 只杀确实是 OpenList.Server 的进程，别的程序一律不动。
free_port() {
  local pid
  pid="$(netstat -ano 2>/dev/null | grep -E ":$PORT .*LISTENING" | awk '{print $NF}' | head -1)"
  [ -z "$pid" ] && return 0
  if tasklist //FI "PID eq $pid" 2>/dev/null | grep -qi "dotnet.exe"; then
    echo "!! port $PORT held by stale dotnet $pid — killing"
    taskkill //F //PID "$pid" >/dev/null 2>&1
    sleep 1
  else
    echo "!! port $PORT held by a non-dotnet process ($pid) — refusing to touch it"
    return 1
  fi
}

start_server() {
  local dir="$1"
  free_port || return 1
  mkdir -p "$dir"
  # 别写成 `( cd "$dir" && cmd & echo $! > pid )`：`&` 会把 `cd && cmd` 整段放到后台，
  # 于是 echo 仍在**调用者**的 cwd 里执行 —— pid 文件落到仓库根，stop_server 拿不到 PID，
  # 服务一轮轮泄漏，下一轮只剩「端口被旧进程占着」这一种症状。
  pushd "$dir" >/dev/null || return 1
  AdminPassword="$PASS" ASPNETCORE_URLS="$BASE" DistDir="$FE/dist" \
    dotnet "$SERVER" >server.log 2>&1 &
  SERVER_PID=$!
  popd >/dev/null || true
  echo "$SERVER_PID" > "$dir/server.pid"
  for _ in $(seq 1 120); do
    if curl -s -m 1 "$BASE/ping" >/dev/null 2>&1; then return 0; fi
    sleep 0.5
  done
  echo "!! backend did not start; last log lines:"
  tail -20 "$dir/server.log"
  return 1
}

stop_server() {
  [ -n "$SERVER_PID" ] || return 0
  kill "$SERVER_PID" 2>/dev/null
  wait "$SERVER_PID" 2>/dev/null
  SERVER_PID=""
}

trap 'stop_server' EXIT

# 只 tee 不 tail：tail 会把输出攒到进程结束，卡住时完全看不到进展。
run_script() { # <log> <cmd...>
  local logf="$1"; shift
  "$@" 2>&1 | tee "$logf"
  return "${PIPESTATUS[0]}"
}

export ADMIN_PASS="$PASS"
# /e2e 这类参数会被 MSYS 当路径改写（变成 E:/e2e），必须关掉转换
mount_setup() { env MSYS_NO_PATHCONV=1 node "$SETUP" mount "$1" "$2"; }
library_setup() { env MSYS_NO_PATHCONV=1 node "$SETUP" library "$1" "$2"; }

# 每轮一个带时间戳的目录：既避开 safe-delete 的 rm 拦截，也保留历史轮次的日志。
STAMP="$(date +%Y%m%d-%H%M%S)"
round_dir() { # <name>
  echo "$RUNS/$1/$STAMP"
}

# 装置目录同样带时间戳。**不复用旧目录**：这些 verify 脚本第一步就是
# 「若目录存在则整棵删除」，而受保护环境对「一次删除 >50 个条目」会拦下来要求确认，
# 于是「跑第二遍」会诡异地挂在建装置那一步。每次新建一个目录既便宜又干净。
fixture_root() { # <suffix>
  echo "G:/Share/ol-v2-$1-$STAMP"
}

begin_round() { # <dir>
  start_server "$1"
}

# node 侧要 /e2e 这种「像路径」的环境变量值，MSYS 会擅自改写，故用 env 前缀关掉转换。
run_node() { # <log> <script> [env...]
  local logf="$1" script="$2"; shift 2
  env MSYS_NO_PATHCONV=1 TARGET=/e2e "$@" node "$script" 2>&1 | tee "$logf"
  return "${PIPESTATUS[0]}"
}

finish_round() { # <label> <code>
  stop_server
  if [ "$2" -eq 0 ]; then SUMMARY+=("PASS  $1"); else SUMMARY+=("FAIL  $1"); FAILED=1; fi
}

# --------------------------------------------------------------------------- 轮次

round_ops() {
  say "round: ops (B 文件管理)"
  local dir root_v2 root_op
  dir="$(round_dir ops)"; mkdir -p "$dir"
  root_v2="$(fixture_root ops-v2)"; root_op="$(fixture_root ops)"
  "$PY" "$GEN" ops "$root_v2" >/dev/null || return 1
  begin_round "$dir" || return 1

  # 顺序有讲究：v2 要求干净装置（44 项），而 ops 一上来就把装置整棵重建并改写。
  # 所以给两者各自一个目录：v2 对着刚生成的那份跑，ops 对着自己的新目录跑。
  mount_setup /e2e "$root_v2" || { stop_server; return 1; }
  run_node "$dir/v2.log" "$FE/scripts/verify-v2.mjs"
  local v2=$?
  # Local 驱动在 create 时会校验根目录存在，所以先建空目录（ops 随后自己重建）
  mkdir -p "$root_op"
  mount_setup /e2e "$root_op" || { stop_server; return 1; }
  export E2E_ROOT="$root_op"
  run_node "$dir/ops.log" "$FE/scripts/verify-ops.mjs"
  local ops=$?
  finish_round "B/ops (verify-v2=$v2 verify-ops=$ops)" $(( v2 + ops ))
}

round_preview() {
  say "round: preview (D 归档 / Office / .url / asciinema)"
  local dir; dir="$(round_dir preview)"; mkdir -p "$dir"
  local root; root="$(fixture_root prev)"
  "$PY" "$GEN" preview "$root" >/dev/null || return 1
  begin_round "$dir" || return 1
  mount_setup /e2e "$root" || { stop_server; return 1; }
  run_node "$dir/run.log" "$FE/scripts/verify-preview.mjs"
  finish_round "D/preview" $?
}

round_preview_extra() {
  say "round: preview-extra (D video360 / flash)"
  local dir; dir="$(round_dir preview-extra)"; mkdir -p "$dir"
  begin_round "$dir" || return 1
  run_node "$dir/run.log" "$FE/scripts/verify-preview-extra.mjs"
  finish_round "D/preview-extra" $?
}

round_editor() {
  say "round: editor (D 文本编辑器)"
  local dir root
  dir="$(round_dir editor)"; mkdir -p "$dir"
  root="$(fixture_root edit)"; mkdir -p "$root"
  begin_round "$dir" || return 1
  mount_setup /e2e "$root" || { stop_server; return 1; }
  export E2E_ROOT="$root"
  run_node "$dir/run.log" "$FE/scripts/verify-editor.mjs"
  finish_round "D/editor" $?
}

round_rename() {
  say "round: rename (B 批量重命名)"
  local dir root
  dir="$(round_dir rename)"; mkdir -p "$dir"
  root="$(fixture_root rename)"; mkdir -p "$root"
  begin_round "$dir" || return 1
  mount_setup /e2e "$root" || { stop_server; return 1; }
  export E2E_ROOT="$root"
  run_node "$dir/run.log" "$FE/scripts/verify-rename.mjs"
  finish_round "B/rename" $?
}

round_torrent() {
  say "round: torrent (D 种子)"
  local dir root
  dir="$(round_dir torrent)"; mkdir -p "$dir"
  root="$(fixture_root torrent)"
  EXPECT_OUT="$dir/expected" "$PY" "$GEN" torrent "$root" >/dev/null || return 1
  begin_round "$dir" || return 1
  mount_setup /e2e "$root" || { stop_server; return 1; }
  # EXPECT 指到本轮生成的 expected.json（装置本身要挂进 VFS，期望值不能出现在里面）
  export EXPECT="$dir/expected/expected.json" FIXTURE="$root"
  run_node "$dir/run.log" "$FE/scripts/verify-torrent.mjs"
  finish_round "D/torrent" $?
}

round_media() {
  say "round: media (F 媒体域浏览 / 详情 / 播放)"
  local dir; dir="$(round_dir media)"; mkdir -p "$dir"
  local root; root="$(fixture_root movies)"
  "$PY" "$GEN" media "$root" >/dev/null || return 1
  begin_round "$dir" || return 1
  mount_setup /e2e "$root" || { stop_server; return 1; }
  library_setup "E2E Movies" /e2e || { stop_server; return 1; }
  run_node "$dir/run.log" "$FE/scripts/verify-media.mjs"
  finish_round "F/media" $?
}

round_media_manage() {
  say "round: media-manage (E 媒体库管理台)"
  local dir; dir="$(round_dir media-manage)"; mkdir -p "$dir"
  # 装置：库根下留 1 个视频 + 脚本自己铺 2000 个 filler → 扫描后聚成 2 个条目
  local root="G:/Share/ol-v2-media"
  rm -rf "$root"; mkdir -p "$root/E2E Movie Night"
  ffmpeg -y -v error -f lavfi -i "testsrc=size=64x36:rate=5:duration=1" \
    -c:v libx264 -pix_fmt yuv420p "$root/root-movie.mp4" || return 1
  begin_round "$dir" || return 1
  run_node "$dir/run.log" "$FE/scripts/verify-media-manage.mjs"
  finish_round "E/media-manage" $?
}

round_dimensions() {
  say "round: dimensions (F 人物 / 片商 / 系列 / 口味 / 播放列表)"
  local dir; dir="$(round_dir dimensions)"; mkdir -p "$dir"
  begin_round "$dir" || return 1
  run_node "$dir/run.log" "$FE/scripts/verify-media-dimensions.mjs"
  finish_round "F/dimensions" $?
}

round_messenger() {
  say "round: messenger (E 管理台站内信 + 用户侧收件箱)"
  local dir; dir="$(round_dir messenger)"; mkdir -p "$dir"
  # 装置自建（幂等建 carol、清空两边历史消息），不需要挂载
  begin_round "$dir" || return 1
  run_node "$dir/run.log" "$FE/scripts/verify-messenger.mjs"
  finish_round "E/messenger" $?
}

round_scrape() {
  say "round: scrape (E 管理台整库刮削按钮)"
  local dir; dir="$(round_dir scrape)"; mkdir -p "$dir"
  # 装置与挂载都由脚本自建（3 个无 NFO 的视频 → 条目落 Pending + 1 个空库 → 按钮置灰）
  begin_round "$dir" || return 1
  run_node "$dir/run.log" "$FE/scripts/verify-scrape.mjs"
  finish_round "E/scrape" $?
}

round_dashboard() {
  say "round: dashboard (E 管理台仪表盘)"
  local dir; dir="$(round_dir dashboard)"; mkdir -p "$dir"
  # 装置用 media 那份：Budapest 带 NFO（已匹配）、NoSidecar 只有 mp4（待刮削），
  # 于是匹配率正好 50%，题材分布有 Comedy/Drama —— 断言才有可区分的真数字。
  local root; root="$(fixture_root dash)"
  "$PY" "$GEN" media "$root" >/dev/null || return 1
  begin_round "$dir" || return 1
  # 挂载由脚本自建（它自己会先查一遍 storage/list，已存在则复用）
  export FIXTURE="$root"
  run_node "$dir/run.log" "$FE/scripts/verify-dashboard.mjs"
  finish_round "E/dashboard" $?
}

round_common() {
  say "round: common (A 认证与守卫 / E 管理台 / G 外观)"
  local dir; dir="$(round_dir common)"; mkdir -p "$dir"
  begin_round "$dir" || return 1
  run_node "$dir/run.log" "$FE/scripts/verify-common.mjs"
  finish_round "AEG/common" $?
}

round_upload() {
  say "round: upload (C 上传管线：流式 / 表单 / 分片)"
  local dir root
  dir="$(round_dir upload)"; mkdir -p "$dir"
  root="$(fixture_root upload)"; mkdir -p "$root"
  begin_round "$dir" || return 1
  mount_setup /e2e "$root" || { stop_server; return 1; }
  # e2e-upload 给了 BASE 就不再自起后端（否则会写进开发库）
  run_node "$dir/run.log" "$FE/scripts/e2e-upload.mjs"
  finish_round "C/upload" $?
}

# --------------------------------------------------------------------------- 入口

WANT=("$@")
[ ${#WANT[@]} -eq 0 ] && WANT=(common ops upload preview preview-extra editor rename torrent media media-manage dimensions scrape dashboard messenger)

for name in "${WANT[@]}"; do
  # 轮次名用连字符（preview-extra），函数名用下划线 —— 这里做一次映射
  "round_${name//-/_}" || true
done

printf '\n\033[1m== SUMMARY ==\033[0m\n'
for line in "${SUMMARY[@]}"; do echo "  $line"; done
exit $FAILED
