# Releasing SqlOS

1. Update the version in `src/SqlOS/SqlOS.csproj`.
2. Build and run the full validation suite:

```bash
dotnet build SqlOS.sln
./scripts/unit-tests.sh
./scripts/integration-tests.sh
./scripts/docs-check.sh
```

3. Manually validate the client-compatibility checklist and paste it into the GitHub release body:

```md
- [x] Hosted owned-app flow validated
- [x] Headless owned-app flow validated
- [x] Portable CIMD flow validated
- [x] Compatibility DCR flow validated
- [x] Protected-resource metadata and audience validation validated
```

4. Publish the GitHub release with the matching version tag.

The publish workflow now blocks package publication if those checked items are missing from the release body.
