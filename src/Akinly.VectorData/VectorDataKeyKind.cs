namespace Akinly.VectorData;

/// <summary>The type of keys a vector database's connector accepts.</summary>
public enum VectorDataKeyKind
{
    /// <summary>
    /// GUIDs, derived from Akinly's ids as name-based (version 5) UUIDs: for Qdrant, PostgreSQL
    /// and most others. The default.
    /// </summary>
    Uuid = 0,

    /// <summary>Strings (text keys), such as <c>cluster-42</c>: for connectors that accept only string keys (such as Redis or Azure AI Search, which are untested).</summary>
    Text = 1,
}
