# Releasing SqlOS

1. Update the version in `src/SqlOS/SqlOS.csproj`.
2. Build and run the full test suite:

```bash
dotnet build SqlOS.sln
dotnet test SqlOS.sln
```

3. Tag the release with the matching version.
