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
        set
        {
            if (SetField(ref _outputPath, value))
            {
                OnPropertyChanged(nameof(HasOutput));
                OnPropertyChanged(nameof(OutputFileName));
            }
        }
    }

    public bool HasOutput => !string.IsNullOrWhiteSpace(OutputPath) && File.Exists(OutputPath);

    public string OutputFileName => HasOutput ? Path.GetFileName(OutputPath!) : "";

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

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
