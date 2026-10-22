# Scripts

## pack-nuget.sh

`pack-nuget.sh` makes the NuGet packages of the three integrations:

- `CamusDB.Aspire.Hosting`
- `CamusDB.Aspire.Client`
- `CamusDB.Aspire.EntityFrameworkCore`

It runs the unit tests, and then it packs the three packages into `artifacts/packages`. It pushes them
to nuget.org only when you ask for it.

The script needs the .NET 10 SDK and bash. You can run it from any folder.

### Usage

```shell
scripts/pack-nuget.sh                               # test, then pack
VERSION=0.2.0 scripts/pack-nuget.sh                 # pack a different version
TESTS=0 scripts/pack-nuget.sh                       # pack without the unit tests
PUSH=1 NUGET_API_KEY=... scripts/pack-nuget.sh      # pack, then push to nuget.org
```

### Environment variables

| Variable | Default | Effect |
| --- | --- | --- |
| `VERSION` | `<Version>` in `Directory.Build.props` | The version of all three packages. A prerelease version such as `0.2.0-preview.1` is valid. |
| `TESTS` | `1` | `1` runs the unit tests before the pack. Any other value skips them. |
| `PUSH` | `0` | `1` pushes the packages after the pack. Any other value does not push. |
| `NUGET_API_KEY` | none | The nuget.org API key. It is necessary when `PUSH=1`. |
| `SOURCE` | `https://api.nuget.org/v3/index.json` | The feed that receives the push. |

### What the script does

1. It reads the version.
2. If `TESTS=1`, it runs the unit tests of `tests/CamusDB.Aspire.Hosting.Tests` and
   `tests/CamusDB.Aspire.Client.Tests`. These tests do not need Docker. If a test fails, the script stops.
3. It deletes the old packages of the same version from `artifacts/packages`.
4. It packs each project in `Release` configuration with `-p:Version=$VERSION`. This property also sets
   the assembly version, so the DLLs agree with the package. `ContinuousIntegrationBuild=true` makes the
   source paths in the symbols independent of the machine.
5. It makes sure that each `.nupkg` and `.snupkg` file exists.
6. If `PUSH=1`, it pushes each `.nupkg`. The push also sends the `.snupkg` symbol package.

The push uses `--skip-duplicate`. nuget.org refuses a version that it already has, so with this option,
a second run can complete a push that failed part of the way. A version that you push stays on
nuget.org. You can unlist it, but you cannot delete it.

### Output

```text
artifacts/packages/
  CamusDB.Aspire.Hosting.<version>.nupkg
  CamusDB.Aspire.Hosting.<version>.snupkg
  CamusDB.Aspire.Client.<version>.nupkg
  CamusDB.Aspire.Client.<version>.snupkg
  CamusDB.Aspire.EntityFrameworkCore.<version>.nupkg
  CamusDB.Aspire.EntityFrameworkCore.<version>.snupkg
```

### Use the packages before you publish them

Add the output folder as a package source. Then another project can reference the packages:

```shell
dotnet nuget add source /path/to/camusdb-aspire/artifacts/packages --name camusdb-aspire-local
dotnet add package CamusDB.Aspire.Hosting --version 0.16.0
```

NuGet keeps a copy of each package in its global cache. If you pack the same version again, delete the
old copy, or the project keeps the old package:

```shell
rm -rf ~/.nuget/packages/camusdb.aspire.hosting/0.16.0
```

To remove the source:

```shell
dotnet nuget remove source camusdb-aspire-local
```

### Release steps

1. Set the new version in `<Version>` in `Directory.Build.props`.
2. Make sure that Docker runs.
3. Run the functional tests. They start real CamusDB containers, and they take about 4 minutes.
   The script does not run them.

   ```shell
   dotnet test --project tests/CamusDB.Aspire.Hosting.Tests --filter-trait "Category=Docker"
   ```

4. Make the packages, and examine them:

   ```shell
   scripts/pack-nuget.sh
   ```

5. Push them:

   ```shell
   TESTS=0 PUSH=1 NUGET_API_KEY=... scripts/pack-nuget.sh
   ```
