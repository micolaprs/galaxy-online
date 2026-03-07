# GalaxyNG — HTML Edition

Браузерная версия классической PBeM-стратегии [GalaxyNG](https://github.com/scumola/GalaxyNG) с поддержкой мультиплеера и LLM-ботов.

## Стек

| Компонент       | Технология                              |
|-----------------|-----------------------------------------|
| Game Engine     | C# 14 / .NET 10 (class library)         |
| Game Server     | ASP.NET 10 + SignalR + MCP 1.1          |
| Web UI          | TypeScript + Vite (собирается в wwwroot)|
| LLM Bot         | C# + OpenAI-compatible API (LM Studio)  |
| Тесты           | xUnit + FluentAssertions                |

---

## Требования

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Node.js 23](https://nodejs.org/) (через nvm: `nvm use 23`)
- _(опционально)_ [LM Studio](https://lmstudio.ai/) с моделью `qwen/qwen3-14b` — для LLM-ботов

---

## Быстрый старт

### 1. Клонировать репозиторий

```bash
git clone <repo-url>
cd galaxy-online
```

### 2. Собрать frontend

```bash
cd src/GalaxyNG.Web
nvm use 23        # или node >= 20
npm install
npm run build     # output → src/GalaxyNG.Server/wwwroot/
```

### 3. Запустить сервер

```bash
cd src/GalaxyNG.Server
dotnet run
```

Сервер запустится на **http://localhost:5000**.

### 4. Открыть игру в браузере

```
http://localhost:5000
```

Нажать **New Game** → ввести название, имя расы, пароль → **Create Game**.
Появится ссылка вида `http://localhost:5000/?game=ABCD1234` — передайте её другим игрокам.

---

## Запуск в режиме разработки

Запускайте сервер и Vite dev-сервер одновременно — изменения в TypeScript отражаются мгновенно:

```bash
# Терминал 1 — backend
cd src/GalaxyNG.Server
dotnet watch run

# Терминал 2 — frontend (proxy на :5000)
cd src/GalaxyNG.Web
npm run dev
```

Откройте **http://localhost:5173** (Vite автоматически проксирует `/api` и `/hubs` на backend).

---

## LLM-бот

### Настройка LM Studio

1. Скачайте и запустите [LM Studio](https://lmstudio.ai/)
2. Загрузите модель `qwen/qwen3-14b` (или любую другую OpenAI-совместимую)
3. Запустите Local Server в LM Studio (по умолчанию `http://localhost:1234`)

### Конфигурация бота

Отредактируйте `src/GalaxyNG.Bot/appsettings.json`:

```json
{
  "Bot": {
    "GameId":    "ABCD1234",   // ← ID игры из браузера
    "RaceName":  "BotRace",    // ← имя расы бота (задаётся при создании игры)
    "Password":  "botpw",      // ← пароль расы бота
    "ServerUrl": "http://localhost:5000",
    "Llm": {
      "BaseUrl":     "http://localhost:1234/v1",
      "Model":       "qwen/qwen3-14b",
      "Temperature": 0.7,
      "MaxTokens":   4096,
      "ApiKey":      "lm-studio"
    }
  }
}
```

### Запуск бота

```bash
cd src/GalaxyNG.Bot
dotnet run
```

Бот будет автоматически опрашивать сервер (каждые 30 сек) и делать ходы через REST API.

---

## MCP-сервер

MCP-сервер встроен в Game Server и доступен по адресу:

```
http://localhost:5000/mcp
```

Доступные инструменты (tools):

| Tool                  | Описание                                          |
|-----------------------|---------------------------------------------------|
| `get_game_info`       | Общая информация об игре                          |
| `get_turn_report`     | Полный отчёт о текущем ходе                       |
| `validate_orders`     | Проверка приказов без отправки                    |
| `submit_orders`       | Отправка приказов                                 |
| `get_forecast`        | Прогноз хода на основе ваших приказов             |
| `calculate_distance`  | Расстояние между планетами                        |
| `calculate_ship_stats`| Расчёт характеристик корабля                     |

Подключить к Claude Desktop (`claude_desktop_config.json`):

```json
{
  "mcpServers": {
    "galaxyng": {
      "url": "http://localhost:5000/mcp"
    }
  }
}
```

---

## Тесты

```bash
dotnet test
```

22 unit-теста покрывают:
- Формулы кораблей (масса, скорость, грузоподъёмность, атака/защита)
- Производство планет и рост населения
- Парсер приказов
- Генератор галактики

---

## REST API

| Метод  | Путь                                | Описание                          |
|--------|-------------------------------------|-----------------------------------|
| POST   | `/api/games`                        | Создать игру                      |
| GET    | `/api/games`                        | Список игр                        |
| GET    | `/api/games/{id}`                   | Информация об игре                |
| POST   | `/api/games/{id}/orders`            | Отправить приказы                 |
| GET    | `/api/games/{id}/report/{race}`     | Получить отчёт о ходе             |
| GET    | `/api/games/{id}/forecast/{race}`   | Прогноз хода                      |
| POST   | `/api/games/{id}/run-turn`          | Выполнить ход (ручной запуск)     |

### Пример: создать игру через curl

```bash
curl -X POST http://localhost:5000/api/games \
  -H "Content-Type: application/json" \
  -d '{
    "name": "MyGame",
    "players": [
      { "name": "Terrans", "password": "secret", "isBot": false },
      { "name": "BotRace", "password": "botpw",  "isBot": true  }
    ],
    "galaxySize": 200,
    "autoRun": false
  }'
```

Ответ:
```json
{
  "gameId": "ABCD1234",
  "joinLink": "http://localhost:5000/?game=ABCD1234",
  "players": [
    { "id": "P1", "name": "Terrans", "password": "secret" },
    { "id": "P2", "name": "BotRace", "password": "botpw"  }
  ],
  "turn": 0
}
```

### Пример: отправить приказы

```bash
curl -X POST http://localhost:5000/api/games/ABCD1234/orders \
  -H "Content-Type: application/json" \
  -d '{
    "raceName": "Terrans",
    "password": "secret",
    "orders": "d Scout 1 0 0 0 0\np P1 Scout",
    "final": true
  }'
```

### Пример: выполнить ход

```bash
curl -X POST http://localhost:5000/api/games/ABCD1234/run-turn
```

---

## Формат приказов

```
; Комментарий начинается с точки с запятой
p <планета> <CAP|MAT|DRIVE|WEAPONS|SHIELDS|CARGO|тип_корабля>  ; производство
d <имя> <drive> <attacks> <weapons> <shields> <cargo>           ; проект корабля
s <группа#> <планета>                                           ; отправить
l <группа#> <CAP|COL|MAT>                                       ; загрузить груз
u <группа#>                                                     ; выгрузить
b <группа#> <кол-во>                                            ; разделить группу
g <группа#>                                                     ; апгрейд
x <группа#>                                                     ; утилизировать
a <раса>                                                        ; союз
w <раса>                                                        ; война
n <планета> <новое_имя>                                         ; переименовать
r <планета> <CAP|COL|MAT|EMP> [назначение]                      ; маршрут
```

---

## Структура проекта

```
galaxy-online/
├── src/
│   ├── GalaxyNG.Engine/       # Игровой движок (C# library)
│   │   ├── Models/            # Game, Player, Planet, Group, ShipType…
│   │   └── Services/          # Generator, Parser, TurnProcessor, Combat…
│   ├── GalaxyNG.Server/       # ASP.NET сервер + MCP
│   │   ├── Controllers/       # REST API
│   │   ├── Hubs/              # SignalR
│   │   ├── Mcp/               # MCP tools
│   │   ├── Services/          # GameService, TurnScheduler
│   │   ├── Data/              # GameStore (JSON-файлы)
│   │   └── wwwroot/           # Собранный frontend (Vite output)
│   ├── GalaxyNG.Bot/          # LLM-бот агент
│   └── GalaxyNG.Web/          # Frontend (TypeScript + Vite)
│       ├── src/
│       │   ├── api/           # REST client, session
│       │   ├── components/    # Lobby, GameView, GalaxyMap, OrdersEditor
│       │   └── types/         # TypeScript типы
│       └── index.html
├── tests/
│   └── GalaxyNG.Engine.Tests/ # 22 unit-теста
└── GalaxyNG.sln
```

---

## Игровые данные

Игровые файлы сохраняются в:

```
~/.galaxyng/games/<gameId>/game.json
```

---

## Ключевые формулы

```
Production   = industry × 0.75 + population × 0.25
Speed        = 20 × drive_tech × drive_mass / ship_mass
Ship mass    = drive + weapons + shields + cargo + max(0, attacks-1) × weapons/2
Cargo cap    = cargo_mass + cargo_mass² / 10  (× cargo_tech)
P[kill]      = attack/defense > 4^random
Attack       = weapons_mass × weapons_tech
Defense      = (shields × shields_tech) / ship_mass^(1/3) × 30^(1/3)
Pop growth   = 8% / turn; избыток → колонисты 8:1
Tech cost    = 5000 prod/+1 (drive/wpn/shd), 2500/+1 (cargo)
Ship cost    = mass × 10 prod + mass materials
```
