# Local Autonomous Coding Agent — Obsidian Inbox + Task Decomposer

Текущий этап: приложение принимает задачи из Obsidian Inbox, декомпозирует их, публикует подзадачи в Queue и выполняет их через ограниченный JSON-протокол инструментов.

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
- `model.baseUrl`, `model.modelId`, `model.requestTimeoutSeconds` — подключение к OpenAI-compatible API.
- `prompts.directoryPath` — необязательная внешняя папка промптов; пустое значение использует путь по умолчанию.
- `runtime.inboxPollIntervalSeconds` — интервал опроса Inbox и Queue.
- `runtime.processTimeoutSeconds` — максимальное время одной сборки/проверки (10–1800 секунд, по умолчанию 600).
- `runtime.maxProcessOutputCharacters` — лимит сохраняемого вывода процесса (4096–262144 символов, по умолчанию 65536).

Повреждённый JSON или неподдерживаемая версия схемы сохраняется в `settings.backup.json`; приложение создаёт настройки по умолчанию. Ошибки доступа к файлам не маскируются сбросом.

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
- Единая доменная модель `UserTask` и `TaskContextBuilder` для сборки отдельных контекстов планирования и исполнения.
- Проверка ключей, зависимостей и циклов в плане.
- Проверка, что исходная заметка не изменилась во время планирования.
- Атомарная публикация набора подзадач.
- Наследование `dependency_policy` родительской задачи всеми подзадачами.
- Восстановление незавершённой декомпозиции после перезапуска.

## Ограничения текущего исполнителя

- OpenAI-compatible adapter пока не использует native Tool Calling: применяется строгий JSON-протокол в обычных сообщениях.
- Доступны list_files, read_file, write_file, dotnet_build и dotnet_test. Удаление файлов и произвольный запуск команд не предоставлены.
- ExecutionService запускает `dotnet` напрямую через ProcessStartInfo.ArgumentList, без CMD/PowerShell; ограничивает тип операции, путь проекта, конфигурацию, время и объём вывода. Отмена останавливает дерево процесса.
- Важно: `dotnet build/test` могут выполнять MSBuild targets и код самого проекта. Запускай агент только на доверенном Workspace; это ограничение команд не является полноценной OS sandbox.
- Валидация пока основана на итоговом ответе модели; отдельный ResultValidator и SelfCorrectionEngine ещё предстоит добавить.
- Worker работает последовательно. Параллельное выполнение не включено, поскольку для него нужна межпроцессная атомарная блокировка claim.

## Следующий этап

- ResultValidator и SelfCorrectionEngine с ограниченным числом попыток.
- File Safety / Recovery: журнал изменений и откат файлов.
- Git Integration с обязательным подтверждением перед Push.
- Обновление ProjectMemory и интеграционные тесты.

Полноценный Workspace Scanner не входит в текущую архитектуру. Исполнитель будет читать только те исходники и конфигурации, которые относятся к конкретной задаче.
