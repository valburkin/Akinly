# Contributing to Akinly

Thank you for helping. This guide covers how to build, what a change must keep, and how to send it.

## Before you start

- **Security problems** go through private reporting, not issues: see [SECURITY.md](SECURITY.md).
- For a larger change (a new option, a new judge or store, a change to the public API), open an issue first so we can agree on the approach. The API follows [docs/design.md](docs/design.md); a change to it updates the design in the same pull request, and a breaking change waits for the next major version.
- Never put private or personal text in tests, fixtures, issues or pull requests. Write made-up examples.

## Building and testing

You need the .NET SDK named in [global.json](global.json) (10.0.404 or a later 10.0 release).

```bash
dotnet build Akinly.slnx
dotnet test Akinly.slnx
rm -rf TestResults && dotnet test Akinly.slnx -- --coverage --coverage-output-format cobertura   # with code coverage
python3 .github/scripts/check_coverage.py TestResults                                             # merged rates
```

Two groups of tests need something outside the repository and are skipped without it:

- `AKINLY_CONTAINERS=1`: the vector store's contract tests against Qdrant and PostgreSQL with pgvector, in Docker containers (CI runs them on Linux).
- `AKINLY_OLLAMA=1` (or the server's address): the end-to-end test against a local Ollama, with the models in `AKINLY_OLLAMA_EMBED` and `AKINLY_OLLAMA_CHAT` (defaults `bge-m3` and `qwen3.5:9b`, which must be pulled).

Benchmarks and the evaluation tool:

```bash
dotnet run -c Release --project benchmarks/Akinly.Benchmarks -- --filter '*'
dotnet run -c Release --project tools/Akinly.Evaluation -- calibrate --dataset banking77 --embedder ollama:bge-m3
dotnet run -c Release --project tools/Akinly.Evaluation -- evaluate --dataset banking77 --embedder ollama:bge-m3 --judge ollama:qwen3.5:9b
```

`dotnet test` runs on Microsoft.Testing.Platform; do not add `-nologo`, which would be passed to the test app. CI runs the same commands on Linux and Windows, checks line and branch coverage against a floor (95% and 90%), and packs the library ([.github/workflows/ci.yml](.github/workflows/ci.yml)).

## What every change keeps

- **Deterministic results.** The same items, clusters, options and judge answers in the same order give the same clusters. No randomness, no dependence on the clock or on hash order.
- **Pure core.** The clustering functions take what they need and return decisions; loading, storing and model calls belong to the caller or to an adapter package.
- **Vectors are compared by direction.** Inputs are normalized, every vector in a call has the same length, and zero and NaN vectors never join, attract or merge.
- **No text in exceptions or logs.** An item's text may be private to whoever clusters it. Name items by id.

## Code conventions

- Analyzers and code style are build errors (`Directory.Build.props`, `.editorconfig`). Turn a rule off only in `.editorconfig`, with a reason.
- Every public member has an XML comment that says what it does and what its parameters mean. CS1591 is an error.
- One main type per file, named after it; namespaces follow folders; `tests/` mirrors `src/`.
- Library code awaits with `ConfigureAwait(false)` and takes a `CancellationToken` on every async method.
- Comments explain why. Write American English in code, comments, docs and commit messages.
- The public API of each package is recorded in its `PublicAPI.Shipped.txt` (released) and `PublicAPI.Unshipped.txt` (since the last release). A change to it fails the build until it is recorded there: apply the analyzer's fix (RS0016, RS0017) or edit the file, and say in the pull request why the change is needed. Removing or changing a shipped member is a breaking change and waits for a major version.
- Package versions live only in `Directory.Packages.props`. After changing one, run `dotnet restore` and commit the updated `packages.lock.json` files; CI restores in locked mode and fails on a stale lock.

## Tests

Every change comes with tests that prove it. Name a test as a sentence about the behavior it pins down (`A_refused_item_is_checked_against_the_next_nearest_then_starts_its_own`). Keep the documentation's examples working: `ReadmeExampleTests` and `DocsExampleTests` (in `tests/Akinly.Tests`) mirror every example in the README and in `docs/` that needs only the core package, so a change to one belongs in both. Examples that need a model or a database are not run; check them against the APIs when you change them. A change users would notice also updates the guide that describes it.

## Pull requests

- Keep a pull request to one change, with a title that says what it does ("Skip merges across refused pairs", not "Fix bug").
- Describe what changed and why, and how you tested it. Fill in the template's checklist.
- Commit messages follow [Conventional Commits](https://www.conventionalcommits.org): `<type>: <summary>`, the summary lowercase and imperative, at most 72 characters, then a blank line and the why.
- Update [CHANGELOG.md](CHANGELOG.md) under "Unreleased" for anything a user of the library would notice.

## Releasing (maintainer)

1. Update the Install sections of the README, `docs/getting-started.md` and each package's README (`src/*/README.md`, the packages' pages on nuget.org) if the packages' status changed.
2. Move the "Unreleased" entries in CHANGELOG.md under the new version and date, and set `<Version>` in `Directory.Build.props`.
3. Move each package's `PublicAPI.Unshipped.txt` entries into its `PublicAPI.Shipped.txt`.
4. Merge, then tag the commit on `main` with `vX.Y.Z` and push the tag. CI builds, tests and packs it, and the publish job pushes the package to nuget.org after the `nuget` environment's approval.
5. Once the packages are on nuget.org, set `<PackageValidationBaselineVersion>` in `Directory.Build.props` to the released version, so pack checks the next release against it for breaking changes.

## License

By contributing, you agree that your contribution is licensed under the [MIT License](LICENSE), like the rest of the project.
