# Задание 2

Нужен .NET SDK 9. Команды выполняются из папки проекта.

```bash
dotnet build -c Release
```

В трёх отдельных терминалах:

```bash
dotnet run --project src/TspWorker -c Release --no-build -- --urls http://127.0.0.1:5101
```

```bash
dotnet run --project src/TspWorker -c Release --no-build -- --urls http://127.0.0.1:5102
```

```bash
dotnet run --project src/TspDistributed.App -c Release --no-build
```

В окне адреса сервисов уже заполнены. Нажать «Старт», для остановки — «Стоп».

Тесты:

```bash
dotnet test -c Release
```
