using PhotoShelver.Core.Importing;

namespace PhotoShelver.App.ViewModels;

/// <param name="LogPath">The import log, or null when it could not be written.</param>
/// <param name="LogError">Why the log could not be (completely) written, or null.</param>
public sealed record ImportFinishedInfo(ImportResult Result, string TargetFolder, string? LogPath, string? LogError);
