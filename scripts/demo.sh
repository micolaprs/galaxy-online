#!/usr/bin/env bash
# demo.sh — запуск тестовой игры
# Использование:
#   ./scripts/demo.sh                # 3 бота, всегда создаёт новую игру
#   ./scripts/demo.sh --resume       # возобновляет последнюю игру, если она не завершена
#                                    # иначе создаёт новую с её же параметрами
#   ./scripts/demo.sh --new          # явный флаг: создать новую игру
#   ./scripts/demo.sh -b -n 5        # только боты, 5 штук
#   ./scripts/demo.sh -n 5           # 5 ботов
#   ./scripts/demo.sh -s 400         # размер галактики
#   ./scripts/demo.sh --no-kill      # не убивать уже запущенные процессы
#   ./scripts/demo.sh --no-clean-logs # не очищать /tmp-логи перед стартом
#   ./scripts/demo.sh --openai-codex --auth-dir ~/.codex
#   ./scripts/demo.sh --provider openai/codex --auth-dir ~/.codex

set -euo pipefail

# ── Параметры ────────────────────────────────────────────────────────────────
GALAXY_SIZE=""
BOTS_ONLY=true
OPEN_BROWSER=true
NUM_BOTS=3
DO_KILL=false   # по умолчанию: не убиваем старые процессы (watch перезапускается сам)
FORCE_NEW=true  # по умолчанию: всегда создаём новую игру
DO_CLEAN_LOGS=false # по умолчанию: не очищаем логи
LLM_PROVIDER="${GALAXYNG_BOT_LLM_PROVIDER:-openai/codex}"
PROVIDER_AUTH_DIR="${GALAXYNG_OPENAI_CODEX_AUTH_DIR:-}"
MAX_TURNS="${GALAXYNG_DEMO_MAX_TURNS:-60}"  # drives galaxy size (more turns = bigger galaxy)
STRICT_CODEX_DEFAULT=true

while [[ $# -gt 0 ]]; do
  case "$1" in
    -b|--bots-only) BOTS_ONLY=true ; shift ;;
    -n|--bots)      NUM_BOTS="$2"; shift 2 ;;
    -s|--size)      GALAXY_SIZE="$2"; shift 2 ;; # explicit override
    -o|--open)      OPEN_BROWSER=true;  shift ;;
    --no-open)      OPEN_BROWSER=false; shift ;;
    --kill)         DO_KILL=true; DO_CLEAN_LOGS=true; shift ;;
    --no-kill)      DO_KILL=false; shift ;;
    --clean-logs)   DO_CLEAN_LOGS=true; shift ;;
    --no-clean-logs) DO_CLEAN_LOGS=false; shift ;;
    --resume)       FORCE_NEW=false; shift ;;
    --new)          FORCE_NEW=true; shift ;;
    --openai-codex) LLM_PROVIDER="openai/codex"; shift ;;
    --provider)     LLM_PROVIDER="$2"; shift 2 ;;
    --auth-dir)     PROVIDER_AUTH_DIR="$2"; shift 2 ;;
    --max-turns)    MAX_TURNS="$2"; shift 2 ;;
    -h|--help)
      echo "Использование: $0 [опции]"
      echo ""
      echo "  По умолчанию: всегда создаёт новую игру."
      echo ""
      echo "  --resume              Возобновить последнюю сохранённую игру,"
      echo "                        а если она завершена — создать новую с её же параметрами"
      echo "  --new                 Явно создать новую игру"
      echo "  -b, --bots-only       Все игроки — боты (без человека)"
      echo "  -n, --bots NUM        Количество ботов (по умолчанию: 3)"
      echo "  -s, --size SIZE       Размер галактики (по умолчанию: 200)"
      echo "  -o, --open            Открыть браузер после запуска (по умолчанию)"
      echo "  --no-open             Не открывать браузер"
      echo "  --kill                Убить старые процессы и очистить логи перед стартом"
      echo "  --no-kill             Не убивать уже запущенные сервер/боты (по умолчанию)"
      echo "  --clean-logs          Очистить /tmp-логи перед стартом"
      echo "  --no-clean-logs       Не очищать /tmp-логи перед стартом (по умолчанию)"
      echo "  --openai-codex        Быстрый флаг: LLM-провайдер openai/codex"
      echo "  --provider NAME       LLM-провайдер: lmstudio | openai/codex"
      echo "  --auth-dir PATH       Путь к папке auth-файлов Codex (для openai/codex)"
      echo "  --max-turns N         Лимит ходов игры (по умолчанию: 60)"
      echo ""
      echo "  Можно задавать через env:"
      echo "    GALAXYNG_BOT_LLM_PROVIDER=lmstudio|openai/codex"
      echo "    GALAXYNG_OPENAI_CODEX_AUTH_DIR=~/.codex"
      echo "    GALAXYNG_DEMO_MAX_TURNS=60"
      echo ""
      echo "По умолчанию: создаём новую игру и открываем браузер."
      exit 0 ;;
    *) echo "Неизвестный флаг: $1" >&2; exit 1 ;;
  esac
done

# ── Конфигурация игроков ─────────────────────────────────────────────────────
SERVER_URL="${GALAXYNG_SERVER_URL:-http://localhost:5055}"
SERVER_BIND_URL="${GALAXYNG_SERVER_BIND_URL:-http://0.0.0.0:5055}"
GAME_NAME="Demo"
HUMAN="Humans"
HUMAN_PW="pw1"

_BASE_NAMES=("Alpha" "Beta" "Gamma" "Delta" "Epsilon" "Zeta" "Eta" "Theta"
             "Iota" "Kappa" "Lambda" "Mu" "Nu" "Xi" "Omicron" "Pi"
             "Rho" "Sigma" "Tau" "Upsilon" "Phi" "Chi" "Psi" "Omega")
BOT_NAME_POOL=(); BOT_PW_POOL=()
for _idx in $(seq 0 $((NUM_BOTS + 1))); do
  if [[ $_idx -lt ${#_BASE_NAMES[@]} ]]; then
    BOT_NAME_POOL+=("${_BASE_NAMES[$_idx]}")
  else
    BOT_NAME_POOL+=("Bot$((_idx + 1))")
  fi
  BOT_PW_POOL+=("pw$((_idx + 1))")
done

if $BOTS_ONLY; then
  ALL_NAMES=(); ALL_PWS=(); BOT_NAMES=(); BOT_PWS=()
  for i in $(seq 0 $((NUM_BOTS - 1))); do
    ALL_NAMES+=("${BOT_NAME_POOL[$i]}")
    ALL_PWS+=("${BOT_PW_POOL[$i]}")
    BOT_NAMES+=("${BOT_NAME_POOL[$i]}")
    BOT_PWS+=("${BOT_PW_POOL[$i]}")
  done
else
  ALL_NAMES=("$HUMAN"); ALL_PWS=("$HUMAN_PW"); BOT_NAMES=(); BOT_PWS=()
  for i in $(seq 0 $((NUM_BOTS - 1))); do
    ALL_NAMES+=("${BOT_NAME_POOL[$i]}")
    ALL_PWS+=("${BOT_PW_POOL[$((i+1))]}")
    BOT_NAMES+=("${BOT_NAME_POOL[$i]}")
    BOT_PWS+=("${BOT_PW_POOL[$((i+1))]}")
  done
fi

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SERVER_DIR="$REPO_ROOT/src/GalaxyNG.Server"
WEB_DIR="$REPO_ROOT/src/GalaxyNG.Web"
BOT_DIR="$REPO_ROOT/src/GalaxyNG.Bot"

expand_home() {
  local p="$1"
  if [[ "$p" == "~/"* ]]; then
    echo "${HOME}/${p#~/}"
  else
    echo "$p"
  fi
}

normalize_provider() {
  echo "$1" | tr '[:upper:]' '[:lower:]'
}

bot_name_index() {
  local name="$1"
  for i in "${!_BASE_NAMES[@]}"; do
    if [[ "${_BASE_NAMES[$i]}" == "$name" ]]; then
      echo "$i"
      return 0
    fi
  done
  if [[ "$name" =~ ^Bot([0-9]+)$ ]]; then
    echo "$((BASH_REMATCH[1] - 1))"
    return 0
  fi
  return 1
}

bot_password_for_name() {
  local name="$1"
  local offset="$2"
  local idx
  idx="$(bot_name_index "$name")" || return 1
  echo "pw$((idx + 1 + offset))"
}

resolve_llm_runtime() {
  local requested
  requested="$(normalize_provider "$LLM_PROVIDER")"

  BOT_LLM_PROVIDER="$requested"
  BOT_LLM_API="chat-completions"
  BOT_LLM_BASE_URL="http://localhost:1234/v1"
  BOT_LLM_MODEL="${GALAXYNG_BOT_LLM_MODEL:-qwen/qwen3.5-4b}"
  BOT_LLM_API_KEY="${GALAXYNG_BOT_LLM_API_KEY:-lm-studio}"
  BOT_LLM_ACCOUNT_ID="${GALAXYNG_BOT_LLM_ACCOUNT_ID:-}"
  BOT_LLM_AUTH_DIR=""

  if [[ "$requested" == "openai/codex" || "$requested" == "openai-codex" ]]; then
    local auth_dir token account_id creds
    auth_dir="$(expand_home "${PROVIDER_AUTH_DIR:-}")"
    token=""
    account_id=""
    if [[ -z "$auth_dir" ]]; then
      auth_dir="$HOME/.codex"
    fi
    if [[ -n "$auth_dir" && -f "$auth_dir/auth.json" ]]; then
      creds="$(python3 - "$auth_dir/auth.json" <<'PY'
import json, sys
p = sys.argv[1]
try:
    with open(p, "r", encoding="utf-8") as f:
        d = json.load(f)
    t = (d.get("tokens") or {}).get("access_token") or ""
    a = (d.get("tokens") or {}).get("account_id") or ""
    if t:
        print(t + "\t" + a)
    else:
        k = d.get("OPENAI_API_KEY") or ""
        print(k + "\t")
except Exception:
    print("\t")
PY
)"
      token="${creds%%$'\t'*}"
      account_id="${creds#*$'\t'}"
    fi

    if [[ -n "$auth_dir" && -n "$token" ]]; then
      BOT_LLM_PROVIDER="openai/codex"
      BOT_LLM_API="responses"
      BOT_LLM_BASE_URL="https://chatgpt.com/backend-api"
      BOT_LLM_MODEL="${GALAXYNG_BOT_LLM_MODEL:-gpt-5.3-codex}"
      BOT_LLM_API_KEY="$token"
      BOT_LLM_ACCOUNT_ID="${BOT_LLM_ACCOUNT_ID:-$account_id}"
      BOT_LLM_AUTH_DIR="$auth_dir"
    else
      echo "ERROR: openai/codex недоступен: не найден валидный auth token." >&2
      echo "Проверь файл: ${auth_dir:-$HOME/.codex}/auth.json" >&2
      echo "Или передай --auth-dir <path> / GALAXYNG_OPENAI_CODEX_AUTH_DIR." >&2
      echo "Если нужен LM Studio, укажи явно: --provider lmstudio" >&2
      exit 1
    fi
  fi

  if [[ "$STRICT_CODEX_DEFAULT" == "true" && "$requested" != "lmstudio" && "$requested" != "openai/codex" && "$requested" != "openai-codex" ]]; then
    echo "ERROR: неизвестный provider '$LLM_PROVIDER'. Используй openai/codex или lmstudio." >&2
    exit 1
  fi

  # Server LLM defaults are synchronized with bot LLM so summaries use same provider.
  SERVER_LLM_PROVIDER="$BOT_LLM_PROVIDER"
  SERVER_LLM_BASE_URL="$BOT_LLM_BASE_URL"
  SERVER_LLM_MODEL="${GALAXYNG_SERVER_LLM_MODEL:-$BOT_LLM_MODEL}"
  SERVER_LLM_API_KEY="${GALAXYNG_SERVER_LLM_API_KEY:-$BOT_LLM_API_KEY}"
  SERVER_LLM_ACCOUNT_ID="${GALAXYNG_SERVER_LLM_ACCOUNT_ID:-$BOT_LLM_ACCOUNT_ID}"
}

# ── Цвета ────────────────────────────────────────────────────────────────────
GREEN='\033[0;32m'; YELLOW='\033[1;33m'; CYAN='\033[0;36m'; RED='\033[0;31m'; NC='\033[0m'
info() { echo -e "${CYAN}▶ $*${NC}"; }
ok()   { echo -e "${GREEN}✓ $*${NC}"; }
warn() { echo -e "${YELLOW}! $*${NC}"; }

SERVER_PID=""
SERVER_MANAGED=false
BOT_PIDS=()

cleanup() {
  echo ""
  info "Останавливаем все фоновые процессы…"

  # SIGTERM известным PID (dotnet watch)
  [[ ${#BOT_PIDS[@]} -gt 0 ]] && kill -TERM "${BOT_PIDS[@]}" 2>/dev/null || true
  $SERVER_MANAGED && [[ -n "$SERVER_PID" ]] && kill -TERM "$SERVER_PID" 2>/dev/null || true

  # Добиваем дочерние dotnet-процессы по имени (dotnet watch порождает их сам)
  pkill -TERM -f "GalaxyNG.Server" 2>/dev/null || true
  pkill -TERM -f "GalaxyNG.Bot"    2>/dev/null || true

  sleep 1

  # Принудительно убиваем оставшихся
  pkill -KILL -f "GalaxyNG.Server" 2>/dev/null || true
  pkill -KILL -f "GalaxyNG.Bot"    2>/dev/null || true
  [[ ${#BOT_PIDS[@]} -gt 0 ]] && kill -KILL "${BOT_PIDS[@]}" 2>/dev/null || true
  $SERVER_MANAGED && [[ -n "$SERVER_PID" ]] && kill -KILL "$SERVER_PID" 2>/dev/null || true

  ok "Готово."
}
trap cleanup EXIT INT TERM

# ── Шаг 0: остановить старые процессы ────────────────────────────────────────
if $DO_KILL; then
  echo ""
  echo -e "${RED}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
fi

if $DO_CLEAN_LOGS; then
  info "Шаг 0л — Очищаем логи в /tmp…"
  : > /tmp/galaxyng-server.log
  rm -f /tmp/galaxyng-bot-*.log
  ok "Логи очищены"
fi

if $DO_KILL; then
  info "Шаг 0а — Останавливаем старые процессы…"

  # Убиваем боты (любые dotnet-процессы с GalaxyNG.Bot)
  BOT_PIDS_OLD=$(pgrep -f "GalaxyNG.Bot" 2>/dev/null || true)
  if [[ -n "$BOT_PIDS_OLD" ]]; then
    echo "$BOT_PIDS_OLD" | xargs kill -9 2>/dev/null || true
    ok "Старые боты остановлены (PID: $(echo "$BOT_PIDS_OLD" | tr '\n' ' '))"
  else
    warn "Активных ботов не найдено"
  fi

  # Убиваем сервер на порту 5055 — сначала SIGTERM, потом SIGKILL
  SERVER_PID_OLD=$(lsof -ti :5055 2>/dev/null || true)
  if [[ -n "$SERVER_PID_OLD" ]]; then
    echo "$SERVER_PID_OLD" | xargs kill    2>/dev/null || true
    sleep 1
    # Если ещё жив — добиваем SIGKILL
    SERVER_PID_ALIVE=$(lsof -ti :5055 2>/dev/null || true)
    [[ -n "$SERVER_PID_ALIVE" ]] && echo "$SERVER_PID_ALIVE" | xargs kill -9 2>/dev/null || true
    # Ждём освобождения порта (до 5 сек)
    for i in $(seq 1 5); do
      lsof -ti :5055 > /dev/null 2>&1 || break
      sleep 1
    done
    ok "Старый сервер остановлен (PID: $SERVER_PID_OLD)"
  else
    warn "Сервер на :5055 не запущен"
  fi
fi

if $DO_KILL; then
  echo -e "${RED}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
  echo ""
fi

# ── Шаг 1: сборка frontend ───────────────────────────────────────────────────
info "Шаг 1/5 — Сборка frontend…"
cd "$WEB_DIR"

export NVM_DIR="${NVM_DIR:-$HOME/.nvm}"
# shellcheck disable=SC1091
[[ -s "$NVM_DIR/nvm.sh" ]] && source "$NVM_DIR/nvm.sh" --no-use
# shellcheck disable=SC1091
[[ -f .nvmrc ]] && nvm use 2>/dev/null || true

npm install --silent
npm run build --silent
ok "Frontend собран → wwwroot/"

# ── Подготовка LLM-конфига до запуска сервера ───────────────────────────────
resolve_llm_runtime
ok "LLM provider: $BOT_LLM_PROVIDER (model: $BOT_LLM_MODEL)"
if [[ -n "$BOT_LLM_AUTH_DIR" ]]; then
  ok "Auth dir: $BOT_LLM_AUTH_DIR"
fi

# ── LLM settings ─────────────────────────────────────────────────────────────
# lmstudio: bots use a SemaphoreSlim for serialization — no stagger needed.
# openai/codex: cloud API handles parallel requests fine.
BOT_LLM_TIMEOUT="${GALAXYNG_BOT_LLM_TIMEOUT:-240}"
BOT_POLL_INTERVAL="${GALAXYNG_BOT_POLL_INTERVAL:-12}"
info "LLM: timeout=${BOT_LLM_TIMEOUT}с, poll=${BOT_POLL_INTERVAL}с"

# ── Шаг 2: запуск сервера (dotnet watch) ─────────────────────────────────────
info "Шаг 2/5 — Запуск сервера…"
cd "$SERVER_DIR"

if curl -sf "$SERVER_URL/api/games" > /dev/null 2>&1; then
  warn "Сервер уже работает на $SERVER_URL — используем существующий"
else
  DOTNET_WATCH_RESTART_ON_RUDE_EDIT=true \
  Llm__Provider="$SERVER_LLM_PROVIDER" \
  Llm__BaseUrl="$SERVER_LLM_BASE_URL" \
  Llm__Model="$SERVER_LLM_MODEL" \
  Llm__ApiKey="$SERVER_LLM_API_KEY" \
  Llm__AccountId="$SERVER_LLM_ACCOUNT_ID" \
    dotnet watch run --no-launch-profile --urls "$SERVER_BIND_URL" \
    >> /tmp/galaxyng-server.log 2>&1 &
  SERVER_PID=$!
  SERVER_MANAGED=true

  info "Ожидаем готовности сервера…"
  MAX_WAIT=90
  for i in $(seq 1 $MAX_WAIT); do
    if curl -sf "$SERVER_URL/api/games" > /dev/null 2>&1; then
      ok "Сервер запущен (PID $SERVER_PID, лог: /tmp/galaxyng-server.log)"
      break
    fi
    if ! kill -0 "$SERVER_PID" 2>/dev/null; then
      echo "Сервер завершился с ошибкой! Лог:" >&2
      cat /tmp/galaxyng-server.log >&2
      exit 1
    fi
    printf "."
    sleep 1
    if [[ $i -eq $MAX_WAIT ]]; then
      echo ""
      echo "Сервер не ответил за ${MAX_WAIT}с! Лог:" >&2
      cat /tmp/galaxyng-server.log >&2
      exit 1
    fi
  done
  echo ""
fi

# ── Шаг 3: создание или возобновление игры ───────────────────────────────────
GAME_ID=""
RESUMED=false

if ! $FORCE_NEW; then
  info "Шаг 3/5 — Ищем последнюю сохранённую игру…"
  EXISTING=$(curl -sf "$SERVER_URL/api/games" 2>/dev/null || echo "[]")
  # Выбираем игру с наибольшим lastTurnRunAt (или просто первую)
  GAME_ID=$(echo "$EXISTING" | python3 -c "
import sys, json
games = json.load(sys.stdin)
if not games:
    print('')
    sys.exit(0)
# Sort by lastTurnRunAt desc (None last)
games.sort(key=lambda g: g.get('lastTurnRunAt') or '', reverse=True)
print(games[0]['id'])
" 2>/dev/null || echo "")

  if [[ -n "$GAME_ID" ]]; then
    GAME_DETAIL=$(curl -sf "$SERVER_URL/api/games/$GAME_ID" 2>/dev/null || echo "{}")
    TURN=$(echo "$GAME_DETAIL" | python3 -c "import sys,json; d=json.load(sys.stdin); print(d.get('turn',0))" 2>/dev/null || echo "0")
    IS_FINISHED=$(echo "$GAME_DETAIL" | python3 -c "import sys,json; d=json.load(sys.stdin); print('true' if d.get('isFinished') else 'false')" 2>/dev/null || echo "false")

    GAME_NAME=$(echo "$GAME_DETAIL" | python3 -c "import sys,json; d=json.load(sys.stdin); print(d.get('name','Demo'))" 2>/dev/null || echo "Demo")
    MAX_TURNS=$(echo "$GAME_DETAIL" | python3 -c "import sys,json; d=json.load(sys.stdin); print(d.get('maxTurns',60))" 2>/dev/null || echo "60")
    GALAXY_SIZE=$(echo "$GAME_DETAIL" | python3 -c "import sys,json; d=json.load(sys.stdin); print(d.get('galaxySize') or '')" 2>/dev/null || echo "")

    ALL_NAMES=(); ALL_PWS=(); BOT_NAMES=(); BOT_PWS=()
    BOTS_ONLY=true
    while IFS=$'\t' read -r player_name is_bot; do
      [[ -z "$player_name" ]] && continue
      ALL_NAMES+=("$player_name")
      if [[ "$is_bot" == "1" ]]; then
        BOT_NAMES+=("$player_name")
      else
        BOTS_ONLY=false
      fi
    done < <(echo "$GAME_DETAIL" | python3 -c "
import sys, json
d = json.load(sys.stdin)
for p in d.get('players', []):
    print(f\"{p.get('name','')}\t{1 if p.get('isBot') else 0}\")
")

    PASSWORD_OFFSET=0
    $BOTS_ONLY || PASSWORD_OFFSET=1
    HUMAN_ASSIGNED=false
    for player_name in "${ALL_NAMES[@]}"; do
      if [[ " ${BOT_NAMES[*]} " == *" ${player_name} "* ]]; then
        pw="$(bot_password_for_name "$player_name" "$PASSWORD_OFFSET")"
        ALL_PWS+=("$pw")
        BOT_PWS+=("$pw")
      else
        if ! $HUMAN_ASSIGNED; then
          ALL_PWS+=("$HUMAN_PW")
          HUMAN_ASSIGNED=true
        else
          ALL_PWS+=("$HUMAN_PW")
        fi
      fi
    done

    if $IS_FINISHED; then
      warn "Последняя игра $GAME_ID завершена — создаём новую с теми же параметрами"
      GAME_ID=""
    else
      RESUMED=true
      ok "Возобновляем игру $GAME_ID (ход $TURN, ботов: ${#BOT_NAMES[@]})"
    fi
  else
    warn "Сохранённых игр нет — создаём новую"
  fi
fi

if [[ -z "$GAME_ID" ]]; then
  if [[ -n "$GALAXY_SIZE" ]]; then
    info "Шаг 3/5 — Создание игры «${GAME_NAME}» (размер: ${GALAXY_SIZE})…"
  else
    info "Шаг 3/5 — Создание игры «${GAME_NAME}» (размер: авто)…"
  fi

  PLAYERS_JSON="["
  for i in "${!ALL_NAMES[@]}"; do
    [[ $i -gt 0 ]] && PLAYERS_JSON+=","
    if $BOTS_ONLY; then
      IS_BOT="true"
    else
      IS_BOT=$( [[ $i -eq 0 ]] && echo "false" || echo "true" )
    fi
    PLAYERS_JSON+=$(printf ' { "name": "%s", "password": "%s", "isBot": %s }' \
      "${ALL_NAMES[$i]}" "${ALL_PWS[$i]}" "$IS_BOT")
  done
  PLAYERS_JSON+=" ]"

  if [[ -n "$GALAXY_SIZE" ]]; then
    SIZE_JSON=",\"galaxySize\":$GALAXY_SIZE"
  else
    SIZE_JSON=""
  fi

  RESPONSE=$(curl -sf -X POST "$SERVER_URL/api/games" \
    -H "Content-Type: application/json" \
    -d "$(printf '{"name":"%s","players":%s,"autoRun":true,"maxTurns":%d%s}' \
      "$GAME_NAME" "$PLAYERS_JSON" "$MAX_TURNS" "$SIZE_JSON")")

  GAME_ID=$(echo "$RESPONSE" | python3 -c "import sys,json; print(json.load(sys.stdin)['gameId'])")
  ok "Игра создана: $GAME_ID"
fi

# ── Шаг 4: запуск ботов (dotnet watch) ───────────────────────────────────────
info "Шаг 4/5 — Запуск ботов…"
cd "$BOT_DIR"

# Если DO_KILL=false и боты уже запущены — не дублируем
if ! $DO_KILL && pgrep -f "GalaxyNG.Bot" > /dev/null 2>&1; then
  warn "Боты уже запущены — пропускаем (используй --kill для перезапуска)"
else
  info "Запуск ${#BOT_NAMES[@]} ботов…"
  for i in "${!BOT_NAMES[@]}"; do
    BOT_NAME="${BOT_NAMES[$i]}"
    BOT_PW="${BOT_PWS[$i]}"
    LOG_FILE="/tmp/galaxyng-bot-${BOT_NAME}.log"
    Bot__GameId="$GAME_ID" \
    Bot__RaceName="$BOT_NAME" \
    Bot__Password="$BOT_PW" \
    Bot__ServerUrl="$SERVER_URL" \
    Bot__LlmTimeoutSeconds="$BOT_LLM_TIMEOUT" \
    Bot__PollIntervalSeconds="$BOT_POLL_INTERVAL" \
    Bot__Llm__Provider="$BOT_LLM_PROVIDER" \
    Bot__Llm__Api="$BOT_LLM_API" \
    Bot__Llm__BaseUrl="$BOT_LLM_BASE_URL" \
    Bot__Llm__Model="$BOT_LLM_MODEL" \
    Bot__Llm__ApiKey="$BOT_LLM_API_KEY" \
    Bot__Llm__AccountId="$BOT_LLM_ACCOUNT_ID" \
    Bot__Llm__AuthFilesDir="$BOT_LLM_AUTH_DIR" \
    DOTNET_WATCH_RESTART_ON_RUDE_EDIT=true \
      dotnet watch run --no-launch-profile >> "$LOG_FILE" 2>&1 &
    BOT_PIDS+=($!)
    ok "Бот $BOT_NAME запущен (PID ${BOT_PIDS[$i]}, лог: $LOG_FILE)"
  done
fi

# ── Шаг 5: итог ──────────────────────────────────────────────────────────────
if $BOTS_ONLY; then
  BROWSER_URL="$SERVER_URL/?watch=$GAME_ID"
else
  BROWSER_URL="$SERVER_URL/?game=$GAME_ID&race=$HUMAN&pw=$HUMAN_PW"
fi

echo ""
echo -e "${GREEN}═══════════════════════════════════════════════════════${NC}"
ok "Игра готова!"
echo ""
$RESUMED && ok "Режим: возобновление игры $GAME_ID" || ok "Режим: новая игра $GAME_ID"
$BOTS_ONLY && ok "autoRun=true, ходы идут автоматически"
echo ""
echo "  Открыть в браузере:"
echo -e "  ${CYAN}$BROWSER_URL${NC}"
echo ""
echo "  Логи:"
echo "    Сервер: /tmp/galaxyng-server.log"
for name in "${BOT_NAMES[@]}"; do
  echo "    Бот $name: /tmp/galaxyng-bot-${name}.log"
done
echo ""
warn "Нажмите Ctrl+C для остановки всех процессов."
echo -e "${GREEN}═══════════════════════════════════════════════════════${NC}"

if $OPEN_BROWSER; then
  info "Открываем браузер…"
  if command -v open &>/dev/null; then
    open "$BROWSER_URL"
  elif command -v xdg-open &>/dev/null; then
    xdg-open "$BROWSER_URL"
  else
    warn "Не удалось открыть браузер автоматически."
  fi
fi

if $SERVER_MANAGED && [[ -n "$SERVER_PID" ]]; then
  wait "$SERVER_PID"
else
  wait "${BOT_PIDS[@]}"
fi
