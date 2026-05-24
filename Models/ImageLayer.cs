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

    public string Detail => $"{Bitmap.PixelWidth} x {Bitmap.PixelHeight}";

    public string OpacityText => $"{Opacity:F0}%";

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
