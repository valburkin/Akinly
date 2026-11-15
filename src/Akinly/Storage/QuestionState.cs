namespace Akinly.Storage;

/// <summary>Whether a question still waits for an answer.</summary>
public enum QuestionState
{
    /// <summary>Waiting for an answer.</summary>
    Open = 0,

    /// <summary>Answered, given up on, or no longer relevant; later answers to it are ignored.</summary>
    Closed = 1,
}
