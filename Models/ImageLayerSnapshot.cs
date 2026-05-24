using System.Windows.Media.Imaging;

namespace Waifu16K.Models;

public sealed record ImageLayerSnapshot(
    string Name,
    BitmapSource Bitmap,
    BitmapSource? Mask,
    int OffsetX,
    int OffsetY,
    bool IsVisible,
    double Opacity,
    ImageBlendMode BlendMode);
