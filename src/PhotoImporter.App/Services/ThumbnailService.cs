using System.Collections.Concurrent;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PhotoImporter.App.ViewModels;

namespace PhotoImporter.App.Services;

/// <summary>
/// Loads thumbnails on background STA threads, most recently requested first (= what is on screen now),
/// and keeps at most <see cref="Capacity"/> of them in memory.
/// </summary>
public sealed class ThumbnailService : IDisposable
{
    private const int Capacity = 1000;
    private const int PixelSize = 256;
    private const int WorkerCount = 2;

    private readonly ConcurrentStack<MediaItemViewModel> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly Dispatcher _ui;
    private readonly LinkedList<MediaItemViewModel> _loaded = new();
    private volatile bool _disposed;

    public ThumbnailService(Dispatcher ui)
    {
        _ui = ui;
        for (var i = 0; i < WorkerCount; i++)
        {
            var thread = new Thread(Worker) { IsBackground = true, Name = $"Thumbnails {i}", Priority = ThreadPriority.BelowNormal };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }
    }

    public void Request(MediaItemViewModel item)
    {
        _queue.Push(item);
        _signal.Release();
    }

    /// <summary>Drops pending requests (e.g. when the source is rescanned).</summary>
    public void ClearQueue() => _queue.Clear();

    /// <summary>Windows' TIFF thumbnail ignores the orientation tag (scans lie on the side); turns it upright.</summary>
    private static BitmapSource UprightTiff(string path, BitmapSource image)
    {
        if (Core.Metadata.EmbeddedPreview.TryReadTiffInfo(path) is not { } info) return image;
        var orientation = Core.Metadata.ThumbnailOrientation.ToApply(
            info.Orientation, info.Width, info.Height, image.PixelWidth, image.PixelHeight);
        if (orientation == 1) return image;
        try
        {
            var result = RawThumbnail.Orient(image, orientation);
            result.Freeze();
            return result;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return image;
        }
    }

    private void Worker()
    {
        while (!_disposed)
        {
            _signal.Wait();
            if (_disposed) return;
            if (!_queue.TryPop(out var item) || !item.IsAlive) continue;

            // RAW: the camera's embedded preview first (fast, needs no codec); otherwise Windows' thumbnail.
            var path = item.File.Path;
            var image = (Core.Metadata.EmbeddedPreview.IsRaw(path) ? RawThumbnail.Get(path, PixelSize) : null)
                        ?? ShellThumbnail.Get(path, PixelSize);
            if (image is not null && Core.Metadata.EmbeddedPreview.IsTiff(path)) image = UprightTiff(path, image);
            _ui.BeginInvoke(DispatcherPriority.Background, () =>
            {
                if (!item.IsAlive) return;
                item.SetThumbnail(image);
                if (image is null) return;
                _loaded.AddLast(item);
                while (_loaded.Count > Capacity)
                {
                    _loaded.First!.Value.EvictThumbnail();
                    _loaded.RemoveFirst();
                }
            });
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _signal.Release(WorkerCount);
    }
}
