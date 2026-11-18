# Local Autonomous Coding Agent — Obsidian Inbox + Task Decomposer

Текущий этап: приложение принимает задачи из Obsidian Inbox, декомпозирует их, публикует подзадачи в Queue, выполняет их через ограниченный JSON-протокол инструментов и проводит независимую проверку с ограниченными попытками исправления.


### Git Integration

- Исполнитель может читать `git_status` и `git_diff`; эти инструменты не меняют репозиторий.
- Git-операции разрешены только если корень репозитория совпадает с корнем выбранного Workspace. Родительские и вложенные репозитории не затрагиваются.
- Локальный commit запускается только отдельной командой пользователя с явным сообщением и списком конкретных относительных файлов (каталоги не принимаются). Перед staging приложение проверяет, что индекс Git пуст, чтобы случайно не включить ранее подготовленные изменения.
- Commit сам по себе не выполняет push и не обращается к сети. Настроенные Git hooks могут запускать пользовательские программы. Push, pull, fetch, reset и checkout агенту недоступны.
- Требуется установленный Git CLI. Команды запускаются напрямую через `ProcessStartInfo.ArgumentList`, без shell, с ограничением времени и вывода.

Проверить статус:

```powershell
dotnet run --project .\backend.csproj -- --git-status
```

Посмотреть diff (untracked-файлы отдельно перечисляются в status):

```powershell
dotnet run --project .\backend.csproj -- --git-diff
```

Создать локальный коммит только для явно перечисленных файлов:

```powershell
dotnet run --project .\backend.csproj -- --git-commit --message "Краткое описание" --path src\File.cs --path README.md
```

Перед коммитом проверь `--git-status` и `--git-diff`. Выбирай только те пути, которые действительно хочешь включить.

### File Safety / Recovery

- Перед первой операцией `write_file` в рамках задачи исходный файл копируется в локальное хранилище восстановления вне Workspace (`LocalApplicationData/LocalCodingAgent/Recovery`). Для новых файлов в журнале фиксируется факт их отсутствия.
- Если задача не проходит независимую проверку после всех попыток исправления, исходные файлы восстанавливаются, а созданные задачей файлы удаляются.
- При следующем запуске приложения незавершённые файловые транзакции откатываются автоматически.
- Успешные снимки сохраняются в recovery-хранилище; автоматическая очистка и ручной интерфейс восстановления пока не реализованы.
- Защита охватывает изменения через `write_file`. Побочные изменения, которые могут производить команды сборки/тестирования (генерируемые файлы и артефакты), в текущей версии не входят в файловую транзакцию.


## Требования

- .NET 10 SDK
- Локальный OpenAI-compatible inference endpoint (по умолчанию LM Studio: `http://localhost:1234/`)
- Локальный Obsidian Vault
- Отдельная директория Workspace проекта

Внешние NuGet-пакеты не используются.

## Конфигурация

При первом запуске приложение создаёт `%LOCALAPPDATA%\LocalCodingAgent\settings.json`.
Промпты по умолчанию копируются в `%LOCALAPPDATA%\LocalCodingAgent\Prompts\` и далее редактируются независимо от сборки приложения.

Основные поля `settings.json`:

- `obsidian.vaultPath` — путь к Obsidian Vault.
- `workspaces.defaultId` и `workspaces.registered` — выбранный Workspace и список разрешённых Workspace.
- `model.activeProfileId` — активный профиль подключения.
- `model.profiles` — сохранённые профили: `id`, `name`, `providerId`, `baseUrl`, `modelId`, `requestTimeoutSeconds`.
- `model.providerId`, `model.baseUrl`, `model.modelId`, `model.requestTimeoutSeconds` — совместимая проекция активного профиля для runtime.
- API-ключи не записываются в JSON: они хранятся в Windows Credential Manager отдельно для каждого профиля.
- `prompts.directoryPath` — необязательная внешняя папка промптов; пустое значение использует путь по умолчанию.
- `runtime.inboxPollIntervalSeconds` — интервал опроса Inbox и Queue.
- `runtime.processTimeoutSeconds` — максимальное время одной сборки/проверки (10–1800 секунд, по умолчанию 600).
- `runtime.maxProcessOutputCharacters` — лимит сохраняемого вывода процесса (4096–262144 символов, по умолчанию 65536).
- `runtime.maxCorrectionAttempts` — число дополнительных попыток исправления после первой проверки (0–10, по умолчанию 3).

Повреждённый JSON или неподдерживаемая версия схемы сохраняется в `settings.backup.json`; приложение создаёт настройки по умолчанию. Ошибки доступа к файлам не маскируются сбросом.

## Build/Test profiles

`BuildTestProfile` определяет профиль только по manifest-файлам в корне выбранного проекта. На текущем этапе поддерживаются:

| Профиль | Сборка | Тесты |
|---|---|---|
| .NET | `dotnet build` | `dotnet test` |
| Node.js | `npm run build`, `pnpm run build` или `yarn build` | `npm run test`, `pnpm run test` или `yarn test` |
| Python | отдельная команда сборки не назначается | `pytest` при его конфигурации, иначе `unittest`, если найдены тесты |
| Go | `go build ./...` | `go test ./...` |
| Rust | `cargo build` | `cargo test` |
| Java / Maven | `mvn package` без тестов | `mvn test` |
| Java / Gradle | `gradle build -x test` | `gradle test` |

Профиль не устанавливает зависимости и не создаёт тестовый фреймворк. Если в Node.js-проекте нет `build`/`test` scripts или в Python-проекте не обнаружены тесты, соответствующая команда считается неприменимой. Для других стеков профиль добавляется отдельным адаптером, без изменения Worker и Validator.

На Windows для известных `.cmd`-лаунчеров npm/pnpm/yarn/Maven/Gradle используется системный `cmd.exe` только с фиксированными аргументами, сформированными профилем. Пользовательский ввод не превращается в команду оболочки. Сборка и тесты всё равно исполняют код доверенного проекта.

## Внешние промпты

Текущая версия использует только три Markdown-промпта:

- `SystemPrompt.md` — общие правила агента.
- `TaskPlanningPrompt.md` — декомпозиция большой задачи.
- `TaskExecutionPrompt.md` — правила исполнителя и формат JSON-вызовов инструментов.

`ProjectScanPrompt.md` больше не является частью обязательной архитектуры и не загружается. Если он остался в пользовательской папке Prompts после старой версии, приложение его просто игнорирует.

## Task-level dependency policy

Политика зависимостей задаётся непосредственно в Markdown-задаче через YAML Frontmatter:

```yaml
---
priority: normal
dependency_policy: existing_only
---
```

Допустимые значения:

- `existing_only` — только уже имеющиеся зависимости и стандартная библиотека.
- `allow_if_necessary` — новые зависимости допустимы, если исполнитель обоснует необходимость.
- `unrestricted` — исполнитель может выбрать подходящие зависимости, но должен обосновать их выбор.

Если поле отсутствует в старой заметке, применяется `existing_only`. Приложение наследует политику в каждую сгенерированную подзадачу и проверяет, что она не была изменена при публикации. Сам Decomposer не сканирует весь проект и не пытается перечислить все фреймворки: при нехватке контекста он может создать узкую исследовательскую подзадачу.

Это правило в промпте является инструкцией модели. Для строгой технической блокировки изменения файлов манифестов в будущем потребуется отдельная проверка в ToolRegistry/Executor.

## Task model and context assembly

`UserTask` is the domain representation of the user's original task. It is independent of Obsidian Markdown and contains task identity, description, Workspace, priority, dependency policy, requirements, target files and acceptance criteria. It does not execute model requests, write files or manage Obsidian statuses.

`TaskContextBuilder` is the single assembly point between storage and model-facing contexts:

- Converts `ObsidianTaskDocument` into `UserTask` and validates the Workspace assignment.
- Builds an isolated `TaskPlanningContext` for the Decomposer.
- Builds an isolated `TaskExecutionContext` for the Executor.
- Loads Project Memory only when the Memory prompt feature is enabled.

Planning and execution keep separate context types and separate prompt requests. The same `UserTask` data is reused without duplicating task fields or copying the task's collections. `TaskExecutor` remains responsible only for execution; `TaskDecomposer` remains responsible for planning and publishing.

## Obsidian Vault structure

Приложение управляет только каталогом `LocalCodingAgent` внутри Vault:

```text
LocalCodingAgent/
├── Inbox/
├── Planning/
├── Queue/
├── InProgress/
├── Completed/
├── Failed/
├── Projects/
└── Reports/
```

- Пользователь создаёт большие задачи в `Inbox`.
- `InboxProcessor` переносит заметку в `Planning`.
- `TaskDecomposer` отправляет отдельный запрос модели и проверяет JSON-план.
- `ObsidianTaskStore` публикует все подзадачи в Queue атомарным перемещением staging-директории.
- `ProjectMemory.md` остаётся компактной смысловой памятью проекта. Обязательного сканера и автоматической инвентаризации файлов нет.
- После успешной независимой проверки задачи `ProjectMemoryUpdater` обновляет память через отдельный внешний промпт `ProjectMemoryUpdatePrompt.md`. Ошибка обновления памяти не отменяет уже завершённую задачу. Повторная обработка одного task ID пропускается по служебному маркеру.

## Запуск

Собрать:

```powershell
dotnet build .\backend.csproj -c Release
```

Настроить `settings.json`, указав существующие `obsidian.vaultPath` и зарегистрированный Workspace.

Однократная обработка:

```powershell
dotnet run --project .\backend.csproj -- --once
```

Постоянный опрос Inbox:

```powershell
dotnet run --project .\backend.csproj
```

Остановка — `Ctrl+C`.

## Реализовано

- Единый `ConfigManager` и `settings.json`.
- Внешние Markdown-промпты.
- OpenAI-compatible provider с Streaming API и Model Discovery.
- Obsidian Task Store и Frontmatter.
- Inbox Processor.
- Отдельный Task Decomposer.
- Task Executor и последовательный Queue Worker с восстановлением незавершённых задач.
- Ограниченный JSON Tool Protocol, Git status/diff (read-only), а также ExecutionService для файловых операций, сборки и тестов по профилю проекта.
- ResultValidator для проверки целевых файлов, путей и результатов сборки/тестов.
- SelfCorrectionEngine с ограниченным числом повторов и передачей диагностики модели.
- Единая доменная модель `UserTask` и `TaskContextBuilder` для сборки отдельных контекстов планирования и исполнения.
- Проверка ключей, зависимостей и циклов в плане.
- Проверка, что исходная заметка не изменилась во время планирования.
- Атомарная публикация набора подзадач.
- Наследование `dependency_policy` родительской задачи всеми подзадачами.
- Восстановление незавершённой декомпозиции после перезапуска.

## Ограничения текущего исполнителя

- OpenAI-compatible adapter пока не использует native Tool Calling: применяется строгий JSON-протокол в обычных сообщениях.
- Доступны list_files, read_file, write_file, project_build, project_test, git_status и git_diff. Удаление файлов и произвольный запуск команд не предоставлены. Локальный Git commit запускается отдельно пользователем с явным списком путей; сетевые Git-команды недоступны.
- ExecutionService запускает только команды, выбранные BuildTestProfile по manifest-файлам: .NET, Node.js (npm/pnpm/yarn), Python, Go, Rust, Java (Maven/Gradle). Процессы запускаются напрямую через ProcessStartInfo.ArgumentList, без CMD/PowerShell и произвольных shell-команд; ограничиваются операции, путь проекта, время и объём вывода. Отмена останавливает дерево процесса.
- Важно: сборка и тесты могут выполнять код самого проекта; npm/pnpm/yarn дополнительно запускают scripts из package.json. Запускай агент только на доверенном Workspace: allow-list команд не является полноценной OS sandbox.
- `ResultValidator` проверяет наличие целевых файлов, безопасные пути и фактические результаты project_build/project_test; для изменённого исходного кода требует успешную сборку, если профиль её предусматривает, и успешные тесты, если профиль обнаружил тестовую команду. Семантическая оценка критериев приёмки не считается доказанной только по наличию файлов.
- `SelfCorrectionEngine` повторяет выполнение с конкретной диагностикой ResultValidator; число дополнительных попыток задаётся `runtime.maxCorrectionAttempts`.
- Worker работает последовательно. Параллельное выполнение не включено, поскольку для него нужна межпроцессная атомарная блокировка claim.

## Следующий этап

- Интеграционные тесты полного цикла Inbox → Planning → Queue → Execution → Validation → Completed.
- При необходимости — отдельный интерфейс подтверждения Git Push; в текущей версии сетевые Git-операции намеренно отсутствуют.

Полноценный Workspace Scanner не входит в текущую архитектуру. Исполнитель будет читать только те исходники и конфигурации, которые относятся к конкретной задаче.

## Model providers

Model connections are stored as named profiles in `settings.json` under `model.profiles`. The selected profile is named by `model.activeProfileId`. The legacy flat fields (`providerId`, `baseUrl`, `modelId`, `requestTimeoutSeconds`) mirror the active profile so existing runtime components and older settings files remain compatible. On first load, a legacy single-connection configuration is migrated into a `local` profile without changing its endpoint or model.

Example:

```json
"model": {
  "activeProfileId": "local",
  "profiles": [
    {
      "id": "local",
      "name": "LM Studio",
      "providerId": "lm-studio",
      "baseUrl": "http://localhost:1234/",
      "modelId": "your-model-id",
      "requestTimeoutSeconds": 300
    }
  ],
  "providerId": "lm-studio",
  "baseUrl": "http://localhost:1234/",
  "modelId": "your-model-id",
  "requestTimeoutSeconds": 300
}
```

`providerId` selects a preset from `Models/ProviderCatalog.cs`. Current presets use the shared OpenAI Chat Completions adapter: `lm-studio`, `ollama`, `vllm`, `localai`, `openai`, `openrouter`, `deepseek`, `groq`, `together`, and `openai-compatible` for custom endpoints. The `baseUrl` is the server root; the adapter appends `v1/models` and `v1/chat/completions`.

The `ModelProviderRegistry` maps API protocols to adapters. A provider using a different protocol can be added by implementing `IModelProvider` (and optionally `IModelDiscovery`) and registering its adapter factory. The agent pipeline continues to depend only on `IModelProvider`.

API keys are stored in Windows Credential Manager under a target derived from the profile ID; they are never written to `settings.json`, command-line arguments, or application logs. The runtime loads the key for the active profile and passes it to the provider adapter. The built-in CLI helpers are:

```powershell
# Show saved profiles and whether a credential exists (never displays the key)
dotnet run --project .\backend.csproj -- --model-profiles

# Add an OpenAI connection profile (the endpoint defaults to the provider preset)
dotnet run --project .\backend.csproj -- --model-profile-add --profile-id openai-main --provider openai --model-id gpt-4.1 --name "OpenAI main"

# Store a key with hidden console input for the active profile
dotnet run --project .\backend.csproj -- --model-key-set

# Store or remove a key for a specific profile
dotnet run --project .\backend.csproj -- --model-key-set --profile openai-main
dotnet run --project .\backend.csproj -- --model-key-delete --profile openai-main

# Switch active profile
dotnet run --project .\backend.csproj -- --model-use openai-main
```

The profile service (`ModelProfileService`) exposes the same operations for the future settings UI. Windows Credential Manager is currently the platform-specific secret backend; no NuGet packages are required.

## TS Desktop (Windows + WebView2)

The desktop shell is a separate WinForms application that hosts the dashboard in Microsoft Edge WebView2 and starts the existing agent backend as a child process.

Build and run from the repository root:

```powershell
dotnet run --project .\Desktop\TaskSandbox.Desktop.csproj
```

The desktop build copies the backend output into its `Agent` subdirectory. Microsoft Edge WebView2 Runtime must be installed on Windows. The backend remains independently runnable with `dotnet run --project .\backend.csproj` for development and diagnostics.

Dashboard assets are intentionally split into `wwwroot\index.html`, `wwwroot\css\styles.css`, and `wwwroot\js\app.js`.


## Stage 4 — Live Runtime Reliability

This build keeps the Inbox → Planning → Queue → Execution pipeline running after
recoverable runtime-cycle failures.

- `AgentRuntime` now catches unexpected cycle errors, writes a `runtime_cycle_error`
  activity event, waits for the configured polling interval, reinitializes the task
  store/recovery state, and resumes polling instead of silently leaving the dashboard
  online with a dead worker.
- A successful runtime initialization writes a `runtime_ready` activity event.
- The Desktop host captures backend stdout/stderr in
  `%LOCALAPPDATA%\\TS\\TaskSandbox\\Logs\\backend.log`.
  If the backend exits during startup, the error dialog includes the log path.
- The Activity page labels the new runtime events.

The runtime starts automatically with the backend. Put a parent request in the
Obsidian `LocalCodingAgent/Inbox` folder; the Decomposer publishes subtasks into
`Queue`, and the Queue Worker claims ready subtasks automatically. A queued task
with unfinished `depends_on` entries remains waiting until those dependencies are
in `Completed`.

Build verification: JavaScript syntax was checked with `node --check`. A .NET build
was not run in this environment because the .NET SDK is unavailable.
