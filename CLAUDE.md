# CLAUDE.md

Guidance for Claude Code when working in this repository.

## What this is

Akinly is a NuGet library: incremental clustering of short texts for .NET. Items join a cluster or start one as they arrive, clusters keep lasting ids, and a judge (a language model, a rule or a person) settles the close calls. Three packages:

- `Akinly`: the core.
- `Akinly.AI`: judges, labels and embeddings through Microsoft.Extensions.AI.
- `Akinly.VectorData`: stores through Microsoft.Extensions.VectorData.

See [README.md](README.md), the guides in [docs/](docs/README.md) and the design record, [docs/design.md](docs/design.md).

The people this serves are developers who add the packages to their apps. A change is judged by what it does for them: a stable API, sensible defaults, clear errors, docs that work as written. The repository is public, so everything in it, history included, must be fit to publish: no references to private projects, machines or people, no API keys.

## Commands

```bash
dotnet build Akinly.slnx
dotnet test Akinly.slnx                          # never add -nologo (see below)
AKINLY_CONTAINERS=1 dotnet test Akinly.slnx      # also the store contract tests on Qdrant and PostgreSQL (Docker)
AKINLY_OLLAMA=1 dotnet test Akinly.slnx          # also the end-to-end test on a local Ollama (AKINLY_OLLAMA_EMBED, AKINLY_OLLAMA_CHAT)
dotnet pack Akinly.slnx -c Release -o artifacts
dotnet run -c Release --project tools/Akinly.Evaluation        # calibrate thresholds, measure quality (prints its usage)
dotnet run -c Release --project benchmarks/Akinly.Benchmarks -- --filter '*'
```

Tests run on Microsoft.Testing.Platform (`global.json`). `dotnet test` forwards unknown flags to the test app, so `-nologo` makes it fail with "Zero tests ran". That is not a broken environment.

CI runs on Linux and Windows. It enforces 95% line and 90% branch coverage (`.github/scripts/check_coverage.py`) and packs the libraries. More in [CONTRIBUTING.md](CONTRIBUTING.md).

## Layout

- `src/Akinly/`: the core. Its only dependencies are Microsoft.Extensions' abstractions and options, and System.Numerics.Tensors for the vector math.
  - `Clusterer` and `IncrementalClusterer`: the entry points.
  - `Engine/Session.cs`: placement, questions, answers, merges and splits.
  - `Storage/`: the store contract and the in-memory store.
  - `DependencyInjection/`.
- `src/Akinly.AI/`:
  - `ChatClientJudge` and `JudgePrompts`: the prompts and answer parsing.
  - `ChatClientLabeler`.
  - `TextClusterer`: texts in, clusters out.
  - `ClusteringPreference`.
  - Telemetry.
- `src/Akinly.VectorData/`: `VectorDataClusterStore`, on any connector that meets the store's requirements.
- `tools/Akinly.Evaluation/` (`akinly-eval`): calibrates thresholds and measures quality on public datasets. Not a package.
- `benchmarks/Akinly.Benchmarks/`: BenchmarkDotNet search and add times by store size.
- `tests/`: one project per package and one for the evaluation tool, mirroring `src/` and `tools/`.

## What every change keeps

- **Deterministic.** The same items, options and judge answers in the same order give the same clusters. No randomness, no clock, no hash order.
- **No text in exceptions, logs or telemetry.** An item's text may be private to the app that clusters it. Name items and clusters by id.
- **A judge's failure is never a "no".** A failed or unparsable answer is handled by `FailedAnswerOptions`. It never counts as a refusal.
- **Provider-neutral.** No code branches on a model provider or a vector database. Everything goes through the two Microsoft abstractions.
- **Options validate themselves.** A bad value throws. Nothing falls back to a default silently.

## The packages' API

- **SemVer.** The public API is tracked in each package's `PublicAPI.Shipped.txt` and `PublicAPI.Unshipped.txt`.
  - Record every change there.
  - Never remove or change a shipped member outside a major version.
  - Prefer adding an option or an overload to changing a signature.
- **Every public member has an XML comment.** CS1591 is an error. Say what it does, what its parameters mean and what it throws.
- **Defaults must work for most callers without tuning.** They're calibrated for `text-embedding-3-small`. Change them only with measurements, and say so in the CHANGELOG.
- **Each package's README** (`src/*/README.md`) is its page on nuget.org. Keep it in step with the package.

## Code conventions

- **Analyzers.** They run at `10.0-recommended` with code style enforced, and warnings fail the build. Turn a rule off only in `.editorconfig`, with a reason.
- **Package versions** live only in `Directory.Packages.props`. Commit the `packages.lock.json` files a restore updates; CI restores in locked mode.
- **Async.** Library code awaits with `ConfigureAwait(false)` and takes a `CancellationToken` on every async method.
- **Files.** One main type per file, named after it. Namespaces follow folders.
- **Comments** explain why.
- **American English** everywhere: code, comments, docs and commit messages.

## Docs travel with code

- **Examples run as tests.** `ReadmeExampleTests` and `DocsExampleTests` mirror the examples in the README and the guides that need only the core. Change both together.
- **User-visible changes** update their guide in `docs/` and add an entry to [CHANGELOG.md](CHANGELOG.md) under "Unreleased".
- **Design decisions** are recorded in [docs/design.md](docs/design.md). Change it with the code that needs it.

## Measuring quality

The evaluation tool calibrates the default thresholds and backs the README's claims. The results are in [docs/evaluation/README.md](docs/evaluation/README.md).

- **Claims match the results.** The README claims no more than those results show, including where Akinly loses.
- **Tuning.** Tune on development data and local models (Ollama).
- **Paid model runs** need the maintainer's approval.
- **API keys** come only from environment variables (`OPENAI_API_KEY`, `ANTHROPIC_API_KEY`). Never write them to a file, a log or a commit.
- **Datasets** are downloaded at run time. Never commit them, the cache (`.eval-cache/`) or `--dump` output.

## Git

These rules are the maintainer's. If you are not the maintainer, follow [CONTRIBUTING.md](CONTRIBUTING.md) (pull requests) instead of this section.

- **History.** `main` stays linear, and each change lands as one coherent commit. Larger work may use a branch, squashed into one commit on `main`.
- **Commit messages** follow [Conventional Commits](https://www.conventionalcommits.org): `<type>: <summary>`, lowercase and imperative, at most 72 characters. Then a blank line and the reason for the change.
- **Releases.** Never push, tag or publish a package unless asked. How to release: [CONTRIBUTING.md](CONTRIBUTING.md#releasing-maintainer).
