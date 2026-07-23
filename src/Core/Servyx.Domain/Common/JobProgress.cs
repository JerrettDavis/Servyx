namespace Servyx.Domain.Common;

/// <summary>
/// A point-in-time progress report for a long-running operation (backup creation, mod download, restore, etc.).
/// </summary>
/// <param name="PercentComplete">Completion percentage in the range 0-100, or <see langword="null"/> if indeterminate.</param>
/// <param name="Message">A short human-readable status message.</param>
/// <param name="BytesProcessed">Bytes processed so far, if the operation is byte-oriented.</param>
/// <param name="TotalBytes">Total expected bytes, if known.</param>
public sealed record JobProgress(double? PercentComplete, string Message, long? BytesProcessed, long? TotalBytes);
