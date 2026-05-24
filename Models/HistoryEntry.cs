using System.Windows.Media.Imaging;

namespace Waifu16K.Models;

public sealed class HistoryEntry
{
    public HistoryEntry(
        int sequence,
        string label,
        string detail,
        BitmapSource bitmap,
        BitmapSource? restoreSource,
        IReadOnlyList<ImageLayerSnapshot> layers,
        int activeLayerIndex)
    {
        Sequence = sequence;
        Label = label;
        Detail = detail;
        Bitmap = bitmap;
        RestoreSource = restoreSource;
        Layers = layers;
        ActiveLayerIndex = activeLayerIndex;
    }

    public int Sequence { get; }

    public string Step => Sequence.ToString("00");

    public string Label { get; }

    public string Detail { get; }

    public BitmapSource Bitmap { get; }

    public BitmapSource? RestoreSource { get; }

    public IReadOnlyList<ImageLayerSnapshot> Layers { get; }

    public int ActiveLayerIndex { get; }
}
