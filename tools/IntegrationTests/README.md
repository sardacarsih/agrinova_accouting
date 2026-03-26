# Integration Tests

Console-based integration test harness for `Accounting.Services.PostgresAccessControlService`.

## Run

```powershell
dotnet run --project tools\IntegrationTests\IntegrationTests.csproj
```

The test harness expects database connection configuration from `DatabaseAuthOptions.FromConfiguration()` (for example `AGRINOVA_PG_CONNECTION`).

## File Structure

- `Program.cs`: entrypoint and ordered test case registration.
- `Program.Tests.Core.cs`: user/journal/accounting/account tests.
- `Program.Tests.Inventory.cs`: inventory and sync tests.
- `Program.CentralSyncMock.cs`: in-process HTTP mock server for central sync tests.
- `Program.Infrastructure.cs`: shared helpers (db access, system settings, common cleanup/assert helpers).

## Add a New Test

1. Implement a `private static async Task Test...Async()` method in the most relevant `Program.Tests.*.cs` file.
2. Register it in `Main()` inside `Program.cs` to keep execution order explicit.
3. Use helpers in `Program.Infrastructure.cs` for setup/cleanup where possible.
4. Keep cleanup in `finally` and preserve no-side-effect behavior for other tests.
