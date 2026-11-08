namespace PhotoImporter.Core;

public enum DateSource
{
    /// <summary>Taken from metadata, local wall-clock time of the shooting place.</summary>
    Metadata,

    /// <summary>Taken from metadata stored in UTC; converted using the computer's time zone (may be off near midnight).</summary>
    MetadataGuessedZone,

    /// <summary>No usable metadata; file modification time is used.</summary>
    FileModified,
}

/// <param name="Local">Local wall-clock time used to pick the destination folder.</param>
public readonly record struct CaptureDate(DateTime Local, DateSource Source);
