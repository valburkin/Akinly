# Akinly documentation

Akinly clusters short texts as they arrive, by embedding similarity, with lasting cluster ids and an optional judge for the close calls. Start with [Getting started](getting-started.md); the [project README](../README.md) has the overview.

## Learn

- [Getting started](getting-started.md): install, the first clusterer, reading results, persisting.
- [Concepts](concepts.md): items, clusters and their lasting ids, centroids from sums, the three zones, questions and verdicts, refusals, precision or recall, merges, splits, maintenance, labels, the store and its change sets, one writer.
- [Configuration](configuration.md): every option with its default, what it does and when to change it; binding from configuration and validation.

## How-to guides

Models:

- [Use a local Ollama model](how-to/use-ollama.md)
- [Use OpenAI, Azure OpenAI or any other provider](how-to/use-openai.md)
- [Cluster without a language model](how-to/cluster-without-a-model.md)
- [Answer questions yourself or through a batch API](how-to/answer-questions-yourself.md)
- [Calibrate thresholds for your embedding model](how-to/calibrate-thresholds.md)

Storage:

- [Keep clusters in Qdrant](how-to/use-qdrant.md)
- [Keep clusters in PostgreSQL with pgvector](how-to/use-postgresql.md)
- [Save and restore the in-memory store](how-to/save-and-restore.md)

Operations:

- [Label clusters](how-to/label-clusters.md)
- [Remove items](how-to/remove-items.md)
- [Keep chosen clusters apart with a merge policy](how-to/merge-policy.md)
- [Run in ASP.NET Core or a worker service](how-to/dependency-injection.md)
- [Observe with OpenTelemetry](how-to/opentelemetry.md)

## Reference

- [Troubleshooting](troubleshooting.md): errors and surprises, their causes and fixes.
- [FAQ](faq.md): short answers to common questions.
- [Design](design.md): the design record, with the decisions and their reasons.
- [Evaluation results](evaluation/README.md): how Akinly scores on public datasets, and how to rerun it.
- API reference: every public type and member has XML documentation, which your IDE shows.
- [Changelog](../CHANGELOG.md) and [contributing](../CONTRIBUTING.md).
