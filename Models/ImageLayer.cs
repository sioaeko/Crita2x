using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;

namespace Waifu16K.Models;

public sealed class ImageLayer : INotifyPropertyChanged
{
    private string _name;
    private BitmapSource _bitmap;
    private bool _isVisible = true;
    private double _opacity = 100;
    private ImageBlendMode _blendMode = ImageBlendMode.Normal;

    public ImageLayer(string name, BitmapSource bitmap)
    {
        _name = name;
        _bitmap = bitmap;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name
    {
        get => _name;
        set => SetField(ref _name, value);
    }

    public BitmapSource Bitmap
    {
        get => _bitmap;
        set
        {
            if (ReferenceEquals(_bitmap, value))
            {
                return;
            }

            _bitmap = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Detail));
        }
    }

    public bool IsVisible
    {
        get => _isVisible;
        set => SetField(ref _isVisible, value);
    }

    public double Opacity
    {
        get => _opacity;
        set
        {
            double clamped = Math.Clamp(value, 0, 100);
            if (Math.Abs(_opacity - clamped) < 0.01)
            {
                return;
            }

            _opacity = clamped;
            OnPropertyChanged();
            OnPropertyChanged(nameof(OpacityText));
        }
    }

    public ImageBlendMode BlendMode
    {
        get => _blendMode;
        set
        {
            if (_blendMode == value)
            {
                return;
            }

            _blendMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(BlendModeText));
        }
    }

    public string Detail => $"{Bitmap.PixelWidth} x {Bitmap.PixelHeight}";

    public string OpacityText => $"{Opacity:F0}%";

    public string BlendModeText => BlendMode switch
    {
        ImageBlendMode.Multiply => "곱하기",
        ImageBlendMode.Screen => "스크린",
        ImageBlendMode.Overlay => "오버레이",
        ImageBlendMode.Darken => "어둡게",
        ImageBlendMode.Lighten => "밝게",
        _ => "일반"
    };

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        OnPropertyChanged(propertyName);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
