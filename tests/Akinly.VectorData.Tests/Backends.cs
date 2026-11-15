using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel.Connectors.InMemory;
using Microsoft.SemanticKernel.Connectors.PgVector;
using Microsoft.SemanticKernel.Connectors.Qdrant;
using Qdrant.Client;
using Testcontainers.PostgreSql;
using Testcontainers.Qdrant;

namespace Akinly.VectorData.Tests;

/// <summary>A vector database to run the store against.</summary>
public interface IBackend : IAsyncDisposable
{
    /// <summary>Whether the database is there to test against.</summary>
    bool Available { get; }

    /// <summary>Whether the database accepts string keys (Qdrant takes only UUIDs and integers).</summary>
    bool SupportsTextKeys { get; }

    /// <summary>A vector store over the backend; a fresh collection prefix keeps tests apart.</summary>
    VectorStore Create();
}

/// <summary>Semantic Kernel's in-memory connector: always available.</summary>
public sealed class InMemoryBackend : IBackend
{
    public bool Available => true;

    public bool SupportsTextKeys => true;

    public VectorStore Create() => new InMemoryVectorStore();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// A database in a container, shared by a test class. The container is built and started only
/// when AKINLY_CONTAINERS is set (CI sets it on Linux), so without Docker nothing touches it.
/// </summary>
public abstract class ContainerBackend<TContainer> : IBackend, IAsyncLifetime
    where TContainer : class, IContainer
{
    protected TContainer? Container { get; private set; }

    public bool Available => Container is not null;

    public virtual bool SupportsTextKeys => true;

    public abstract VectorStore Create();

    protected abstract TContainer Build();

    public async ValueTask InitializeAsync()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AKINLY_CONTAINERS")))
        {
            Container = Build();
            await Container.StartAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Container is not null)
        {
            await Container.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }
}

/// <summary>Qdrant in a container.</summary>
public sealed class QdrantBackend : ContainerBackend<QdrantContainer>
{
    public override bool SupportsTextKeys => false;

    public override VectorStore Create() =>
        new QdrantVectorStore(new QdrantClient(Container!.Hostname, Container.GetMappedPublicPort(6334)), ownsClient: true);

    protected override QdrantContainer Build() => new QdrantBuilder("qdrant/qdrant:v1.15.1").Build();
}

/// <summary>PostgreSQL with pgvector in a container.</summary>
public sealed class PostgresBackend : ContainerBackend<PostgreSqlContainer>
{
    public override VectorStore Create() => new PostgresVectorStore(Container!.GetConnectionString());

    protected override PostgreSqlContainer Build() => new PostgreSqlBuilder("pgvector/pgvector:0.8.0-pg17").Build();
}
