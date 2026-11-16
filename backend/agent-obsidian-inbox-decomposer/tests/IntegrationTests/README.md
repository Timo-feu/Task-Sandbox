# Integration tests

Набор интеграционных smoke-тестов без xUnit/NUnit и без дополнительных NuGet-пакетов. Использует временные каталоги и тестовый `IModelProvider`, поэтому не обращается к реальному API модели и не затрагивает пользовательский Obsidian Vault.

Запуск из корня проекта:

```powershell
dotnet run --project .\tests\IntegrationTests\IntegrationTests.csproj -c Release
```

Проверяются:

1. Полный локальный цикл `Inbox → Planning → Queue → Execution → Validation → Project Memory → Completed`.
2. Обычный откат файлов и восстановление незавершённой recovery-сессии.
3. Git status, diff и явный локальный commit.

Тесты требуют .NET 10 SDK и Git CLI. Все данные создаются во временном каталоге и удаляются после выполнения.
