# ClientRenderer: заметки для ИИ-агентов

Воркер распределённого рендера osu!-реплеев в видео (data plane). Пользовательская документация лежит в `README.md`:
архитектура, таблица эндпоинтов сервера, содержимое `settings/`, инструкции по релизу. Здесь описано то, чего
из README и кода быстро не понять.

Связанные репозитории (обычно лежат рядом, в `~/Desktop`):

- `SosuWeb`: в нём `SosuWeb.Render`, control plane с очередью, назначением задач, хранением и политикой версий.
  Его `AGENTS.md` описывает серверную сторону контракта.
- `SosuBot`: Telegram-бот, ставит задачи рендера.

## Карта решения (`ClientRenderer.slnx`)

| Проект | TFM | Роль |
|---|---|---|
| `ClientRenderer` | net9.0 | Ядро без UI: `Connection/ServerConnection.cs` (JWT, heartbeat, HTTP API), `Startup/RenderWorker.cs` (цикл задач), `RenderPipeline/` (реплей, beatmapset, скин, `VideoRenderer`, `ThumbnailRenderer`), `Startup/AppStoragePaths.cs`, `Logging/Logger.cs` (статический Serilog). |
| `ClientRenderer.GUI` | net9.0 | Основной продукт, только он и выпускается. Avalonia 12, MVVM на CommunityToolkit. `Services/RendererService.cs` собирает DI, скачивает и обновляет danser-go и experimental renderer. |
| `ClientRenderer.CLI` | net10.0 | Headless-хост. Бэкенды сам не скачивает (ждёт их рядом с собой), CI его не собирает и не выпускает. |
| `DanserWrapper` | net9.0 | Запуск danser-go и правка `danser/settings/default.json`. |
| `ExperimentalRendererWrapper` | net9.0 | Запуск `osu-replay-viewer-continued` (форк Shoukox, все режимы игры) и его конфиг. |

Ветка по умолчанию и рабочая: `avalonia`. Ветка `main` старая. Release workflow слушает только `avalonia`.

## Цикл задачи

`RenderWorker.RunAsync` → `VideoRenderer.RenderVideo` идут строго в таком порядке:

1. В простое проверить обновления (запрос сервера или раз в час, см. «Обновления»).
2. `get-next-render-job`: `ServerConnection` опрашивает каждые 2 с, `RenderWorker` добавляет паузу 5 с.
3. Проверить, не отменена ли задача, и запустить монитор отмены (опрос `get-render-job-info` каждые 2 с).
4. Скачать реплей, beatmapset и скин.
5. Записать конфиг бэкенда (`DanserGo.AdjustConfig` / `ExperimentalRenderer.AdjustConfig`) и запустить процесс.
6. Отправить видео чанками по 5 MiB, затем превью (ошибки глотаются), длительность через ffprobe,
   метаданные (ошибки глотаются) и `finish-rendering`.

Неочевидное:

- Бэкенд выбирается так: experimental, если `UseExperimentalRenderer || UseAutoPlay`, иначе danser-go. Autoplay
  умеет только experimental renderer.
- `SkinsDownloader` перезаписывает `RenderSettings.Encoder` локальным энкодером (`ChosenRenderingEncoder`).
  Энкодер из задачи сервера игнорируется.
- Любое исключение в пайплайне превращается в `Failure(jobId, e.Message, rerender: false)`: задача закрывается
  провалом, клиент её в очередь не возвращает. Падение бэкенда сохраняет stdout/stderr в
  `logs/danser_*.log` / `logs/experimental-renderer_*.log`.
- Прогресс парсится из stdout процесса (у danser это `Progress: N%`) и отправляется раз в секунду. Во время скачивания
  карты и загрузки видео прогресс не отправляется, а сервер закрывает задачу со `Stuck` после 10 минут без
  обновлений. Длинные новые этапы должны вызывать `ReportRenderingProgress`.
- Готовое видео называется `{BeatmapHash}.mp4` в каталоге videos и перезаписывается следующей задачей с той же картой.

## Контракт с `SosuWeb.Render`

- Heartbeat уходит каждые 10 с с заголовком `X-Client-Renderer-Version`. Значение берётся из
  `ClientRendererVersion.Current`: `InformationalVersion` **entry assembly** без `+metadata`. Если сервер не
  получает heartbeat 30 с, он считает рендерер offline и возвращает его задачу в очередь.
- Токен обновляется на 90% от `expires_in`.
- Сервер выдаёт задачи только версиям `>= ClientRenderer:LatestVersion` (настройка в SosuWeb), остальным отвечает 426.
  Отсюда следует:
  - Локальная Debug-сборка GUI репортит `<Version>` из `ClientRenderer.GUI.csproj`. Если она ниже серверной,
    задач не будет.
  - CLI репортит **свою** версию `1.2.2` из `ClientRenderer.CLI.csproj`, поэтому против текущего сервера (2.0.x)
    задач не получает никогда.
- Коды ответов входят в контракт. `HandleExpectedRendererStateResponseAsync` молча пропускает 400/403/404/409 для
  progress/finish/metadata/failure. `GetNextRenderJob` трактует 400/404/409/426 как «задачи нет, опросить снова».
  Обработку нового кода от сервера добавляй сюда.
- Отменой считается `IsComplete && !IsSuccess && FailureReason == "Cancelled"` (без учёта регистра).
- `Models/RenderJob.cs` и `Models/RenderSettings.cs` копируют модели `SosuWeb.Database`, у `RenderSettings` есть
  ещё копия в SosuBot. Новое поле добавляй во все три копии с одинаковым значением по умолчанию и маппь в
  `Helpers/RenderSettingsExtensions.cs`. Для danser нужно ещё поле в `DanserWrapper/DanserConfiguration.cs` и
  запись в `DanserGo.AdjustConfig`. Для experimental renderer нужен класс в `ExperimentalRendererWrapper/Configuration/`.
- На сервере скин называется `HEX(ASCII(SkinName))`, причём `SkinName` уже содержит `.osk`, а к hex ещё раз
  дописывается `.osk`. Локально каталог скина danser называется первыми 16 hex-символами SHA-256 от имени без
  `.osk` (`HashedSkinName`). `SkinName` всегда равен `default` или заканчивается на `.osk`, на этом держится срез `[..^4]`.

## Пути хранения

Корни задаёт `Startup/AppStoragePaths.cs`, пути не хардкодь. `DanserWrapper` и `ExperimentalRendererWrapper`
повторяют эту логику у себя (на ядро они сослаться не могут, потому что ядро ссылается на них), так что меняй все
три места согласованно.

- Linux: всё лежит в `$XDG_DATA_HOME/ClientRenderer` или `~/.local/share/ClientRenderer` (`settings/`, `logs/`,
  `videos/`, `downloads/`, `danser/`, `experimental-renderer/`, `settings.json` GUI). AppImage смонтирован read-only.
- Windows: `settings/`, `logs/`, `videos/`, `downloads/` лежат в корне Velopack-установки (без Velopack в
  `AppContext.BaseDirectory`). `danser/` и `experimental-renderer/` лежат в `AppContext.BaseDirectory`, то есть
  внутри `current/` у Velopack. `settings.json` GUI лежит в `%LocalAppData%\ClientRenderer`.
- `ConfigurationLoader` один раз переносит старые `BaseDirectory/settings` на новое место. Если файлов настроек
  нет, он создаёт заготовки и бросает `InvalidOperationException` («Set up the app»).

## Бэкенды рендера

- danser-go закреплён на версии `0.11.0` (константы в `ClientRenderer.GUI/Services/RendererService.cs`). Если его нет, GUI
  скачивает архив. `danser/settings/default.json` создаёт сам danser при первом запуске, а перед каждой задачей
  `DanserGo.AdjustConfig` переписывает в нём выбранные ключи. `ClientRenderer.GUI/Assets/default.json` кладётся в output, но код
  его не читает.
- Experimental renderer берётся из latest release `Shoukox/osu-replay-viewer-continued`. Проверка идёт на старте и
  не чаще раза в час в простое. Установленная версия записана в маркер `.renderer-version`, запасная версия `0.9.4`.
  Если GitHub недоступен, остаётся уже установленная версия.
- ffmpeg/ffprobe берутся из каталога experimental renderer (`ExperimentalRenderer.FfmpegPath` / `FfprobePath`).
  На Linux обёртки сами выставляют путь к библиотекам ffmpeg для дочернего процесса.
- На Windows `WindowsGpuPreferenceHelper` прописывает бэкендам и ffmpeg предпочтение дискретной GPU, а
  `WindowsSleepPreventerHelper` на время рендера запрещает сон.

## GUI

- Включены compiled bindings (`AvaloniaUseCompiledBindingsByDefault`), поэтому каждому view нужен `x:DataType`.
- Локализация лежит в `Services/Localization/Strings.resx` / `.de.resx` / `.ru.resx`. В XAML строка берётся как
  `{Binding Source={x:Static app:App.Localizer}, Path=[Key]}`, в коде как `App.Localizer["Key"]`. Отсутствующий
  ключ молча возвращает сам ключ и сборку не ломает, поэтому новые ключи добавляй во все три файла.
- `RendererService` и `UpdateService` синглтоны (`.Instance`). Смена настроек сервера или энкодера идёт через
  `RendererService.RestartAsync`.
- Окно одно на систему (`SingleInstanceManager`, named pipe). С аргументом `--startup` приложение стартует
  свёрнутым в трей.

## Обновления

- Есть два пути, оба через Velopack с источником `GithubSource` на `Shoukox/ClientRenderer`:
  `ClientRenderer.GUI/Services/UpdateService.cs` проверяет при старте UI, `ClientRenderer/Startup/VelopackAutomaticUpdateService.cs` вызывается
  из `RenderWorker` в простое. Обновление применяется только между задачами.
- Без установки через Velopack (`IsInstalled == false`, в том числе при `dotnet run`) обновления пропускаются.
- `packId` = `SosuBot.ClientRenderer`, каналы `win` и `linux`. Если сменить `packId` или канал, установленные клиенты
  перестанут обновляться.

## Сборка и релиз

- Сборка: `dotnet build ClientRenderer.slnx -c Release`. Она проходит с несколькими ожидаемыми предупреждениями
  (`Microsoft.Extensions.DependencyInjection` 11 официально не поддерживает net9.0, CA1416, CS0219). Тестов нет.
- Версия `vpk` в `release.yml` и README совпадает с версией пакета `Velopack`. Обновляй их вместе.
- Скачивание beatmapset: провайдеры перебираются по порядку из `BeatmapsetsDownloader`, тело архива читается
  с контролем зависания и скорости, медленное зеркало на 30 минут уходит в конец очереди. Sayobot не умеет искать
  по хэшу и берёт id сета у провайдера перед ним, поэтому Syui должен стоять раньше.
- CI (`.github/workflows/release.yml`) ставит только .NET 9 SDK и собирает только GUI. Ядро, GUI и обёртки держи на
  `net9.0`, пока не обновлён `setup-dotnet` в workflow.
- Релиз запускается коммитом с subject `release: X.Y.Z`, запушенным в `avalonia`. По соглашению тот же коммит
  поднимает `<Version>`, `<InformationalVersion>` и `<ReleaseDate>` в `ClientRenderer.GUI.csproj` (скрипт
  `scripts/publish-gui-release.ps1` переопределяет их через `-p:`, но локальные сборки репортят версию из csproj).
  Такой коммит публикует GitHub Release для всех рендереров, поэтому делай его только по просьбе владельца.
- После выхода релиза нужно поднять `ClientRenderer:LatestVersion` в `SosuWeb.Render` (другой репозиторий) и
  пересобрать его образ. Только это заставляет работающие рендереры обновиться. Подробнее в `SosuWeb/AGENTS.md`.
- `publish-update-commands.txt` в GUI и CLI устарели, актуален только скрипт.

## Секреты

`settings/cookie.txt` (`osu_session`), `settings/osu-api.json` и `settings/renderer-settings.json` содержат секреты.
Не выводи их содержимое в ответы и не коммить. `ClientRenderer.CLI/renderer-settings.json` отслеживается git:
держи там только заглушки.
