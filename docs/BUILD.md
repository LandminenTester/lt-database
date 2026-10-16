# Build, test and release

## Requirements

* .NET 10 SDK. The projects target `net10.0` with the `wasi-wasm` runtime identifier and compile ahead of time with
  NativeAOT-LLVM, which comes from NuGet (`nuget.config` adds the `dotnet-experimental` feed for the two ILCompiler packages).
* Internet access on the first build to restore packages. Package versions are pinned: `replay.blackice.server` / `replay.blackice.shared`
  1.1.0 and `Serde` 0.10.2.
* Optional: Docker, to run the API and databases for a runtime test.

Windows and Linux both work. Building the WASM takes a few minutes the first time.

## Build

```bash
dotnet build sv/sv.csproj -c Release
```

Output: `sv/bin/Release/net10.0/wasi-wasm/native/sv.wasm`; `Directory.Build.targets` also copies the manifest and the blob to
`dist/`. Check that `dist/config.toml` and `dist/sv.wasm` exist and that the manifest names only `sv.wasm` (no `client_blob`).
A desktop-only compilation does not prove a valid bundle: the WASM publish step has to succeed.

Project layout:

```
config.toml is data/config.toml        the manifest (copied to dist/)
sh/            shared project           Contracts.cs (MessagePack records)  +  Pure/ (crypto, JSON, signing, builders)
sv/            server component         App.cs, DbClient.cs, DatabaseConfig.cs
import/        helper for consumers     LtDatabase.Server.cs
examples/      consumer, smoke          compilable examples
tests/         Bundle.Tests             protocol tests on a normal .NET runtime
vfs/           database.example.json    sample settings (the real files are never committed)
```

`sh/Pure` has no dependency on the BlackICE SDK on purpose, so the tests can compile the same files and run them on a desktop runtime.

## Test

```bash
cd tests/Bundle.Tests
dotnet test
```

39 tests: SHA-256 and HMAC against `System.Security.Cryptography` (every length around the block size) and RFC 4231; the API's published
signing vectors; response verification (forged, replayed, inconsistent, unsigned); the JSON reader and writer (hostile text, malformed
input, nesting); filter, schema, row and `immediate` builders.

Functional test inside a real server: build [`examples/smoke`](../examples/smoke/README.md), install it as a bundle, run
`db-smoke <label>`. It runs 38 steps against whatever database your API uses and prints PASS/FAIL per step.

## Release

1. Update the version in `data/config.toml` (`[General] version`) and in the changelog if you keep one.
2. `dotnet test` in `tests/Bundle.Tests`, `dotnet build sv/sv.csproj -c Release`, `dotnet build examples/consumer/consumer.csproj -c Release`.
3. Create the ZIP with Python 3.11 or later (the packager checks the WASM header and includes only public files):

```bash
python scripts/package.py
```

4. Push a new `v<version>` tag. The workflow tests and builds the connector and both examples, packages the ZIP with
   `SHA256SUMS.txt`, and publishes the GitHub release only after that build passes. Check both jobs before announcing it.

The GitHub Actions workflow in `.github/workflows` runs on main pushes, pull requests and version tags using Windows
and .NET 10. It uploads the package as a CI artifact on ordinary builds. A CI build is not a BlackICE runtime test.

## Contributing

* Keep `sh/Pure` free of SDK dependencies and add a test for every change in it.
* Anything that touches signing, parsing of answers or the configuration needs a test that fails without the change.
* Never log payloads, row values or the secret.
* Keep the documentation in `docs/` and the examples in `examples/consumer/PlayerPreferences.cs` in step with the code; the examples are
  compiled so they cannot drift silently.
* Open pull requests with a short description of the problem and how you tested it (desktop tests, and a runtime test if the change
  touches `sv/` or the helper).
