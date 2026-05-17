using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace Waifu16K.Models;

public sealed class BatchJob : INotifyPropertyChanged
{
    private string _status = "대기";
    private int _progress;
    private bool _isRunning;
    private string? _outputPath;

    public BatchJob(string filePath)
    {
        FilePath = filePath;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string FilePath { get; }

    public string FileName => Path.GetFileName(FilePath);

    public string? OutputPath
    {
        get => _outputPath;
        set => SetField(ref _outputPath, value);
    }

    public string Status
    {
        get => _status;
        set => SetField(ref _status, value);
    }

    public int Progress
    {
        get => _progress;
        set => SetField(ref _progress, Math.Clamp(value, 0, 100));
    }

    public bool IsRunning
    {
        get => _isRunning;
        set => SetField(ref _isRunning, value);
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
