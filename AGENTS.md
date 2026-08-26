# Repository Guidelines

Local development Aspire integration for SqlDataPack. The design and the reasoning behind it are in
`docs/superpowers/specs/2026-08-25-sqldatapack-aspire-hosting-design.md`. Read that before changing
behaviour; it records API facts that are easy to get wrong.

## Traps that have already bitten

- `InteractionInputCollection.GetBoolean` and `GetString` throw when an input has no value, which is
  what an unchecked checkbox looks like. Always read arguments through `CommandArguments`.
- `InteractionFile` has only an internal constructor and `InteractionInput.Files` a non-public
  setter, so a File input cannot be supplied by an MCP or CLI client, and cannot be faked in a test.
- `CustomResourceSnapshot.HealthStatus` has a private setter, which is why the availability rule is
  a pure function over two values rather than a lambda over a snapshot.
- Only `PromptProgressAsync` and friends are experimental. `ASPIREINTERACTION001` belongs in
  `AspireProgressScope.cs` and nowhere else.

## Commands

```bash
dotnet test tests/SqlDataPack.Aspire.Hosting.Tests/SqlDataPack.Aspire.Hosting.Tests.csproj          # fast
dotnet test tests/SqlDataPack.Aspire.Hosting.IntegrationTests/SqlDataPack.Aspire.Hosting.IntegrationTests.csproj  # needs Docker
dotnet tool restore && build/cleanup.sh                                                             # formatter
```

After a version change in `Directory.Packages.props`, run
`dotnet restore SqlDataPack.Aspire.slnx --force-evaluate` and commit the refreshed lock files.
CI restores with `--locked-mode`.

## Releasing

```bash
dotnet run build/VersionGuard.cs -- 1.0.0    # prints the next two lines for you
git tag -a v1.0.0 -m "v1.0.0"
git push origin v1.0.0
```

The tag push starts `.github/workflows/release.yml`, which is the only file allowed to reach
`dotnet nuget push`. It resolves the version from the tag and passes it into build and pack, so
nothing carries a `<Version>` in source. A preview is the same three commands with
`1.0.0-preview.1`; prerelease-ness is read off the version string.

Publishing uses OIDC trusted publishing, so there is no API key secret. It needs the
`NUGET_USER` repository variable set to the nuget.org account name, and a trusted publishing
policy on nuget.org naming this repository and `release.yml`.
