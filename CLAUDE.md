# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project state

This is a freshly scaffolded ASP.NET Core Web API project (`dotnet new webapi`), targeting .NET 10. It currently contains only the default template code (`WeatherForecastController`) and has not yet been built out into an actual email notification service. There is no README, no test project, and this is not yet a git repository.

## Commands

Run all commands from the repository root (where `EmailNotificationService.slnx` lives), or from the `EmailNotificationService/` project directory.

- Build: `dotnet build`
- Run (HTTP profile, port 5242): `dotnet run --project EmailNotificationService --launch-profile http`
- Run (HTTPS profile, ports 7277/5242): `dotnet run --project EmailNotificationService --launch-profile https`
- Restore packages: `dotnet restore`

There is no test project yet. When one is added, `dotnet test` will run it.

Manual API requests can be made using `EmailNotificationService/EmailNotificationService.http` (works with the VS Code REST Client extension or Visual Studio's built-in HTTP file support).

## Architecture

- Solution file: `EmailNotificationService.slnx` (the newer XML-based slnx format, not `.sln`) references the single project `EmailNotificationService/EmailNotificationService.csproj`.
- `Program.cs` uses the ASP.NET Core minimal hosting model: builder → service registration (`AddControllers`, `AddOpenApi`) → app → middleware pipeline (`MapOpenApi` in Development, `UseHttpsRedirection`, `UseAuthorization`, `MapControllers`) → `app.Run()`.
- Controllers live in `Controllers/` and use attribute routing (`[Route("[controller]")]`).
- Nullable reference types and implicit usings are enabled project-wide (set in the `.csproj`).
- OpenAPI/Swagger document generation is enabled via `Microsoft.AspNetCore.OpenApi`, exposed only when `ASPNETCORE_ENVIRONMENT=Development`.
