namespace Akinly.VectorData;

/// <summary>A store whose writes can be stopped between steps, for tests of crash recovery.</summary>
internal interface IWriteSteps
{
    /// <summary>
    /// Called after each step of a write (<c>parts</c>, <c>journal</c>, <c>clusters</c>,
    /// <c>items</c>, <c>questions</c>, <c>info</c>, <c>unjournal</c>); an exception thrown here
    /// stops the write there.
    /// </summary>
    Func<string, Task>? OnStep { get; set; }
}
