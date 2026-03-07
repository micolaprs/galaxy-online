#!/usr/bin/env bash
# demo.sh — запуск тестовой игры
# Использование:
#   ./scripts/demo.sh                # 3 бота, возобновляет последнюю игру (или создаёт новую)
#   ./scripts/demo.sh --new          # всегда создавать новую игру (удаляет старые)
#   ./scripts/demo.sh -b -n 5        # только боты, 5 штук
#   ./scripts/demo.sh -n 5           # 5 ботов
#   ./scripts/demo.sh -s 400         # размер галактики
#   ./scripts/demo.sh --no-kill      # не убивать уже запущенные процессы
#   ./scripts/demo.sh --openai-codex --auth-dir ~/.codex
#   ./scripts/demo.sh --provider openai/codex --auth-dir ~/.codex

set -euo pipefail

# ── Параметры ────────────────────────────────────────────────────────────────
GALAXY_SIZE=200
BOTS_ONLY=true
OPEN_BROWSER=true
NUM_BOTS=3
DO_KILL=true    # по умолчанию: убиваем старые процессы
DO_CLEAN=false  # по умолчанию: возобновляем последнюю игру
FORCE_NEW=false # --new: удалить старые и создать новую
LLM_PROVIDER="${GALAXYNG_BOT_LLM_PROVIDER:-lmstudio}"
PROVIDER_AUTH_DIR="${GALAXYNG_OPENAI_CODEX_AUTH_DIR:-}"

while [[ $# -gt 0 ]]; do
  case "$1" in
    -b|--bots-only) BOTS_ONLY=true ; shift ;;
    -n|--bots)      NUM_BOTS="$2"; shift 2 ;;
    -s|--size)      GALAXY_SIZE="$2"; shift 2 ;;
    -o|--open)      OPEN_BROWSER=true;  shift ;;
    --no-open)      OPEN_BROWSER=false; shift ;;
    --no-kill)      DO_KILL=false; shift ;;
    --new)          FORCE_NEW=true; DO_CLEAN=true; shift ;;
    --openai-codex) LLM_PROVIDER="openai/codex"; shift ;;
    --provider)     LLM_PROVIDER="$2"; shift 2 ;;
    --auth-dir)     PROVIDER_AUTH_DIR="$2"; shift 2 ;;
    -h|--help)
      echo "Использование: $0 [опции]"
      echo ""
      echo "  По умолчанию: возобновляет последнюю сохранённую игру."
      echo "  Если игр нет — создаёт новую."
      echo ""
      echo "  --new                 Всегда создавать новую игру (удаляет старые)"
      echo "  -b, --bots-only       Все игроки — боты (без человека)"
      echo "  -n, --bots NUM        Количество ботов (по умолчанию: 3)"
      echo "  -s, --size SIZE       Размер галактики (по умолчанию: 200)"
      echo "  -o, --open            Открыть браузер после запуска (по умолчанию)"
      echo "  --no-open             Не открывать браузер"
      echo "  --no-kill             Не убивать уже запущенные сервер/боты"
      echo "  --openai-codex        Быстрый флаг: LLM-провайдер openai/codex"
      echo "  --provider NAME       LLM-провайдер: lmstudio | openai/codex"
      echo "  --auth-dir PATH       Путь к папке auth-файлов Codex (для openai/codex)"
      echo ""
      echo "  Можно задавать через env:"
      echo "    GALAXYNG_BOT_LLM_PROVIDER=lmstudio|openai/codex"
      echo "    GALAXYNG_OPENAI_CODEX_AUTH_DIR=~/.codex"
      echo ""
      echo "По умолчанию: убиваем старые процессы + возобновляем последнюю игру + открываем браузер."
      exit 0 ;;
    *) echo "Неизвестный флаг: $1" >&2; exit 1 ;;
  esac
done

# ── Конфигурация игроков ─────────────────────────────────────────────────────
SERVER_URL="http://localhost:5055"
GAME_NAME="Demo"
HUMAN="Humans"
HUMAN_PW="pw1"

BOT_NAME_POOL=("Alpha" "Beta" "Gamma" "Delta" "Epsilon" "Zeta" "Eta" "Theta")
BOT_PW_POOL=("pw1" "pw2" "pw3" "pw4" "pw5" "pw6" "pw7" "pw8")

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
GAMES_DIR="${HOME}/.galaxyng/games"

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

resolve_llm_runtime() {
  local requested
  requested="$(normalize_provider "$LLM_PROVIDER")"

  BOT_LLM_PROVIDER="$requested"
  BOT_LLM_API="chat-completions"
  BOT_LLM_BASE_URL="http://localhost:1234/v1"
  BOT_LLM_MODEL="${GALAXYNG_BOT_LLM_MODEL:-qwen/qwen3.5-9b}"
  BOT_LLM_API_KEY="${GALAXYNG_BOT_LLM_API_KEY:-lm-studio}"
  BOT_LLM_AUTH_DIR=""

  if [[ "$requested" == "openai/codex" || "$requested" == "openai-codex" ]]; then
    local auth_dir token
    auth_dir="$(expand_home "${PROVIDER_AUTH_DIR:-}")"
    token=""
    if [[ -z "$auth_dir" ]]; then
      auth_dir="$HOME/.codex"
    fi
    if [[ -n "$auth_dir" && -f "$auth_dir/auth.json" ]]; then
      token="$(python3 - "$auth_dir/auth.json" <<'PY'
import json, sys
p = sys.argv[1]
try:
    with open(p, "r", encoding="utf-8") as f:
        d = json.load(f)
    t = (d.get("tokens") or {}).get("access_token") or ""
    if t:
        print(t)
    else:
        print(d.get("OPENAI_API_KEY") or "")
except Exception:
    print("")
PY
)"
    fi

    if [[ -n "$auth_dir" && -n "$token" ]]; then
      BOT_LLM_PROVIDER="openai/codex"
      BOT_LLM_API="responses"
      BOT_LLM_BASE_URL="https://chatgpt.com/backend-api"
      BOT_LLM_MODEL="${GALAXYNG_BOT_LLM_MODEL:-gpt-5.3-codex}"
      BOT_LLM_API_KEY="$token"
      BOT_LLM_AUTH_DIR="$auth_dir"
    else
      warn "Провайдер openai/codex недоступен (нет valid auth.json в --auth-dir / GALAXYNG_OPENAI_CODEX_AUTH_DIR). Переходим на LM Studio."
    fi
  fi
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
  [[ ${#BOT_PIDS[@]} -gt 0 ]] && kill "${BOT_PIDS[@]}" 2>/dev/null || true
  $SERVER_MANAGED && [[ -n "$SERVER_PID" ]] && kill "$SERVER_PID" 2>/dev/null || true
  wait 2>/dev/null || true
  ok "Готово."
}
trap cleanup EXIT INT TERM

# ── Шаг 0: остановить старые процессы и/или удалить игры ─────────────────────
if $DO_KILL || $DO_CLEAN; then
  echo ""
  echo -e "${RED}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
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

if $DO_CLEAN; then
  info "Шаг 0б — Удаляем сохранённые игры…"
  if [[ -d "$GAMES_DIR" ]]; then
    GAME_COUNT=$(find "$GAMES_DIR" -maxdepth 1 -mindepth 1 -type d 2>/dev/null | wc -l | tr -d ' ')
    rm -rf "$GAMES_DIR"
    ok "Удалено игр: $GAME_COUNT (${GAMES_DIR})"
  else
    warn "Папка игр не найдена — уже чисто"
  fi
fi

if $DO_KILL || $DO_CLEAN; then
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

# ── Шаг 2: сборка и запуск сервера ───────────────────────────────────────────
info "Шаг 2/5 — Сборка и запуск сервера…"
cd "$SERVER_DIR"
dotnet build -c Release -v quiet 2>&1 | tail -3

if curl -sf "$SERVER_URL/api/games" > /dev/null 2>&1; then
  warn "Сервер уже работает на $SERVER_URL — используем существующий"
else
  dotnet run -c Release --no-build --no-launch-profile --urls "http://localhost:5055" \
    > /tmp/galaxyng-server.log 2>&1 &
  SERVER_PID=$!
  SERVER_MANAGED=true

  info "Ожидаем готовности сервера…"
  MAX_WAIT=60
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
    # Получаем список игроков через spectate
    SPECTATE=$(curl -sf "$SERVER_URL/api/games/$GAME_ID/spectate" 2>/dev/null || echo "{}")
    TURN=$(echo "$SPECTATE" | python3 -c "import sys,json; d=json.load(sys.stdin); print(d.get('turn',0))" 2>/dev/null || echo "0")
    # Собираем ботов из игры (имена)
    BOT_NAMES_IN_GAME=$(echo "$SPECTATE" | python3 -c "
import sys, json
d = json.load(sys.stdin)
bots = [p['name'] for p in d.get('players', []) if p.get('isBot')]
print(' '.join(bots))
" 2>/dev/null || echo "")

    # Сопоставляем ботов из игры с пулом паролей
    BOT_NAMES=(); BOT_PWS=()
    for bot_name in $BOT_NAMES_IN_GAME; do
      for i in "${!BOT_NAME_POOL[@]}"; do
        if [[ "${BOT_NAME_POOL[$i]}" == "$bot_name" ]]; then
          BOT_NAMES+=("$bot_name")
          if $BOTS_ONLY; then
            BOT_PWS+=("${BOT_PW_POOL[$i]}")
          else
            BOT_PWS+=("${BOT_PW_POOL[$((i+1))]}")
          fi
          break
        fi
      done
    done

    RESUMED=true
    ok "Возобновляем игру $GAME_ID (ход $TURN, ботов: ${#BOT_NAMES[@]})"
  else
    warn "Сохранённых игр нет — создаём новую"
  fi
fi

if [[ -z "$GAME_ID" ]]; then
  info "Шаг 3/5 — Создание игры «${GAME_NAME}» (размер: ${GALAXY_SIZE})…"

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

  RESPONSE=$(curl -sf -X POST "$SERVER_URL/api/games" \
    -H "Content-Type: application/json" \
    -d "$(printf '{"name":"%s","players":%s,"galaxySize":%d,"autoRun":true}' \
      "$GAME_NAME" "$PLAYERS_JSON" "$GALAXY_SIZE")")

  GAME_ID=$(echo "$RESPONSE" | python3 -c "import sys,json; print(json.load(sys.stdin)['gameId'])")
  ok "Игра создана: $GAME_ID"
fi

# ── Шаг 4: сборка и запуск ботов ─────────────────────────────────────────────
info "Шаг 4/5 — Сборка ботов…"
cd "$BOT_DIR"
dotnet build -c Release -v quiet 2>&1 | tail -3

resolve_llm_runtime
ok "LLM provider: $BOT_LLM_PROVIDER (model: $BOT_LLM_MODEL)"
if [[ -n "$BOT_LLM_AUTH_DIR" ]]; then
  ok "Auth dir: $BOT_LLM_AUTH_DIR"
fi

info "Запуск ${#BOT_NAMES[@]} ботов…"
for i in "${!BOT_NAMES[@]}"; do
  BOT_NAME="${BOT_NAMES[$i]}"
  BOT_PW="${BOT_PWS[$i]}"
  LOG_FILE="/tmp/galaxyng-bot-${BOT_NAME}.log"
  Bot__GameId="$GAME_ID" \
  Bot__RaceName="$BOT_NAME" \
  Bot__Password="$BOT_PW" \
  Bot__ServerUrl="$SERVER_URL" \
  Bot__Llm__Provider="$BOT_LLM_PROVIDER" \
  Bot__Llm__Api="$BOT_LLM_API" \
  Bot__Llm__BaseUrl="$BOT_LLM_BASE_URL" \
  Bot__Llm__Model="$BOT_LLM_MODEL" \
  Bot__Llm__ApiKey="$BOT_LLM_API_KEY" \
  Bot__Llm__AuthFilesDir="$BOT_LLM_AUTH_DIR" \
    dotnet run -c Release --no-build --no-launch-profile >> "$LOG_FILE" 2>&1 &
  BOT_PIDS+=($!)
  ok "Бот $BOT_NAME запущен (PID ${BOT_PIDS[$i]}, лог: $LOG_FILE)"
done

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
