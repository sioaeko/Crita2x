using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Waifu16K.Models;
using Waifu16K.Services;

namespace Waifu16K;

public partial class MainWindow : Window
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;
    private const int DwmwcpRound = 2;

    private readonly ObservableCollection<BatchJob> _jobs = [];
    private readonly ObservableCollection<HistoryEntry> _historyEntries = [];
    private readonly ObservableCollection<ImageLayer> _layers = [];
    private readonly string[] _supportedExtensions = [".png", ".jpg", ".jpeg", ".webp", ".bmp", ".tif", ".tiff"];
    private readonly Stack<ImageState> _undoStack = new();
    private readonly Stack<ImageState> _redoStack = new();
    private const double MinZoom = 0.05;
    private const double MaxZoom = 16.0;

    private BitmapSource? _currentBitmap;
    private BitmapSource? _originalBitmap;
    private BitmapSource? _restoreSourceBitmap;
    private BitmapSource? _compareBaselineBitmap;
    private string? _currentPath;
    private string? _lastOutputPath;
    private CancellationTokenSource? _queueCancellation;

    private bool _cropMode;
    private bool _isCropping;
    private bool _pickChroma;
    private bool _eraseMode;
    private bool _restoreMode;
    private bool _autoRestoreMode;
    private bool _maskHideMode;
    private bool _maskRevealMode;
    private bool _brushHistoryCaptured;
    private bool _isPanning;
    private Point _panStart;
    private double _panStartHorizontalOffset;
    private double _panStartVerticalOffset;
    private double _zoom = 1.0;
    private Point _cropStart;
    private Point? _lastBrushPoint;
    private Color _chromaColor = Colors.White;
    private int _historyIndex = -1;
    private int _historySequence;
    private bool _suppressHistorySelection;
    private bool _compareMode;
    private bool _suppressLayerSelection;
    private bool _suppressLayerRefresh;
    private bool _suppressLayerControlUpdate;
    private bool _layerOpacityEditCaptured;
    private bool _layerVisibilityEditCaptured;

    public MainWindow()
    {
        InitializeComponent();
        JobList.ItemsSource = _jobs;
        HistoryList.ItemsSource = _historyEntries;
        LayerList.ItemsSource = _layers;
        ResizeSlider.ValueChanged += (_, _) => ResizeBox.Text = ((int)ResizeSlider.Value).ToString();
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        ApplyNativeWindowStyle();
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        UpdateMaximizeGlyph();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeRestore_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void UpdateMaximizeGlyph()
    {
        if (MaximizeButton is null)
        {
            return;
        }

        MaximizeButton.ToolTip = WindowState == WindowState.Maximized ? "복원" : "최대화";
    }

    private void ApplyNativeWindowStyle()
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        int enabled = 1;
        int cornerPreference = DwmwcpRound;
        int captionColor = ToColorRef(0x10, 0x10, 0x12);
        int borderColor = ToColorRef(0x2E, 0x2D, 0x31);
        int textColor = ToColorRef(0xF4, 0xF0, 0xE8);

        _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref enabled, Marshal.SizeOf<int>());
        _ = DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref cornerPreference, Marshal.SizeOf<int>());
        _ = DwmSetWindowAttribute(handle, DwmwaCaptionColor, ref captionColor, Marshal.SizeOf<int>());
        _ = DwmSetWindowAttribute(handle, DwmwaBorderColor, ref borderColor, Marshal.SizeOf<int>());
        _ = DwmSetWindowAttribute(handle, DwmwaTextColor, ref textColor, Marshal.SizeOf<int>());
    }

    private static int ToColorRef(byte r, byte g, byte b)
    {
        return r | (g << 8) | (b << 16);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int attributeValue, int attributeSize);

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        EnginePathBox.Text = Waifu2xService.FindDefaultEngine();
        OutputFolderBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Crita2x");
        RefreshModelChoices();
        await LoadGpuChoicesAsync();
        UpdateQueueText();
        UpdatePreview(null, null);
        UpdateLastOutputActions();
        SetStatus(string.IsNullOrWhiteSpace(EnginePathBox.Text)
            ? "Waifu2x 엔진 경로를 선택하면 업스케일을 실행할 수 있습니다."
            : "준비됨");
    }

    private void OpenImages_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "이미지 추가",
            Multiselect = true,
            Filter = "Images|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.tif;*.tiff|All files|*.*"
        };

        if (dialog.ShowDialog(this) == true)
        {
            AddImages(dialog.FileNames);
        }
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            AddImages(files);
        }
    }

    private void BrowseEngine_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "waifu2x-ncnn-vulkan.exe 선택",
            Filter = "waifu2x-ncnn-vulkan.exe|waifu2x-ncnn-vulkan.exe|Executable|*.exe|All files|*.*"
        };

        if (dialog.ShowDialog(this) == true)
        {
            EnginePathBox.Text = dialog.FileName;
            RefreshModelChoices();
            SetStatus("엔진 경로가 설정되었습니다.");
        }
    }

    private void EnginePathBox_LostFocus(object sender, RoutedEventArgs e)
    {
        RefreshModelChoices();
    }

    private void RefreshModels_Click(object sender, RoutedEventArgs e)
    {
        RefreshModelChoices(showStatus: true);
    }

    private void ModelBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateModelInfo();
    }

    private void BrowseModelFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "waifu2x 모델 폴더 선택",
            InitialDirectory = Directory.Exists(Path.GetDirectoryName(EnginePathBox.Text))
                ? Path.GetDirectoryName(EnginePathBox.Text)
                : Environment.CurrentDirectory
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        if (!Waifu2xService.IsModelDirectory(dialog.FolderName))
        {
            SetStatus("모델 폴더에는 .param/.bin 파일이 필요합니다.");
            MessageBox.Show(this, "선택한 폴더에서 waifu2x 모델 파일(.param/.bin)을 찾지 못했습니다.", "모델 폴더 오류", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        AddModelChoice(new Waifu2xModelOption($"Custom · {Path.GetFileName(dialog.FolderName)}", dialog.FolderName), select: true);
        SetStatus($"모델 추가: {Path.GetFileName(dialog.FolderName)}");
    }

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "저장 폴더 선택",
            InitialDirectory = Directory.Exists(OutputFolderBox.Text)
                ? OutputFolderBox.Text
                : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
        };

        if (dialog.ShowDialog(this) == true)
        {
            OutputFolderBox.Text = dialog.FolderName;
        }
    }

    private async void RunSelected_Click(object sender, RoutedEventArgs e)
    {
        if (JobList.SelectedItem is BatchJob job)
        {
            await RunJobsAsync([job]);
        }
    }

    private async void RunQueue_Click(object sender, RoutedEventArgs e)
    {
        await RunJobsAsync(_jobs.ToArray());
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _queueCancellation?.Cancel();
        SetStatus("중지 요청됨");
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        RestoreHistory(_undoStack, _redoStack, "실행 취소");
    }

    private void Redo_Click(object sender, RoutedEventArgs e)
    {
        RestoreHistory(_redoStack, _undoStack, "다시 실행");
    }

    private void ClearQueue_Click(object sender, RoutedEventArgs e)
    {
        _jobs.Clear();
        UpdateQueueText();
        ClearCurrentImage();
    }

    private void RemoveSelected_Click(object sender, RoutedEventArgs e)
    {
        if (JobList.SelectedItem is not BatchJob job)
        {
            return;
        }

        int index = JobList.SelectedIndex;
        _jobs.Remove(job);
        UpdateQueueText();

        if (_jobs.Count == 0)
        {
            ClearCurrentImage();
        }
        else
        {
            JobList.SelectedIndex = Math.Clamp(index, 0, _jobs.Count - 1);
        }
    }

    private void JobList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (JobList.SelectedItem is not BatchJob job)
        {
            return;
        }

        string path = File.Exists(job.OutputPath) ? job.OutputPath! : job.FilePath;
        LoadImage(path);
    }

    private void OpenJobOutput_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (!TryGetJobFromSender(sender, out BatchJob job) || !TryGetOutputPath(job, out string outputPath))
        {
            SetStatus("열 수 있는 완료 이미지가 없습니다.");
            return;
        }

        if (!ReferenceEquals(JobList.SelectedItem, job))
        {
            JobList.SelectedItem = job;
        }
        else
        {
            LoadImage(outputPath);
        }

        SetLastOutputPath(outputPath);
        SetStatus($"완료 이미지 열기: {Path.GetFileName(outputPath)}");
    }

    private void RevealJobOutput_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (!TryGetJobFromSender(sender, out BatchJob job) || !TryGetOutputPath(job, out string outputPath))
        {
            SetStatus("탐색기에서 열 완료 파일이 없습니다.");
            return;
        }

        RevealFileInExplorer(outputPath);
        SetLastOutputPath(outputPath);
        SetStatus($"탐색기에서 표시: {Path.GetFileName(outputPath)}");
    }

    private void OpenLastOutput_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetLastOutputPath(out string outputPath))
        {
            SetStatus("열 수 있는 최근 완료 이미지가 없습니다.");
            return;
        }

        SelectJobByOutput(outputPath);
        LoadImage(outputPath);
        SetStatus($"최근 완료 이미지 열기: {Path.GetFileName(outputPath)}");
    }

    private void RevealLastOutput_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetLastOutputPath(out string outputPath))
        {
            SetStatus("탐색기에서 열 최근 완료 파일이 없습니다.");
            return;
        }

        RevealFileInExplorer(outputPath);
        SetStatus($"탐색기에서 표시: {Path.GetFileName(outputPath)}");
    }

    private void RemoveBg_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureBitmap())
        {
            return;
        }

        PushUndo();
        BitmapSource edited = BackgroundRemovalService.RemoveBorderBackground(
            GetEditableBitmap()!,
            (int)ToleranceSlider.Value,
            (int)SoftnessSlider.Value);
        SetEditableBitmap(edited);
        RecordHistory("가장자리 누끼");
        SetStatus("가장자리 배경을 투명 처리했습니다.");
    }

    private void PickColor_Click(object sender, RoutedEventArgs e)
    {
        _pickChroma = true;
        _eraseMode = false;
        _restoreMode = false;
        _autoRestoreMode = false;
        _maskHideMode = false;
        _maskRevealMode = false;
        UpdateBrushButtons();
        SetStatus("캔버스에서 제거할 색상을 클릭하세요.");
    }

    private void ChromaKey_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureBitmap())
        {
            return;
        }

        PushUndo();
        BitmapSource edited = BackgroundRemovalService.ChromaKey(
            GetEditableBitmap()!,
            _chromaColor,
            (int)ToleranceSlider.Value,
            (int)SoftnessSlider.Value);
        SetEditableBitmap(edited);
        RecordHistory("색상 누끼");
        SetStatus($"색상 누끼 적용: RGB({_chromaColor.R}, {_chromaColor.G}, {_chromaColor.B})");
    }

    private void Feather_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureBitmap())
        {
            return;
        }

        PushUndo();
        SetEditableBitmap(BackgroundRemovalService.FeatherAlpha(GetEditableBitmap()!, radius: 2));
        RecordHistory("가장자리 정리");
        SetStatus("가장자리 알파를 부드럽게 정리했습니다.");
    }

    private void Defringe_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureBitmap())
        {
            return;
        }

        PushUndo();
        SetEditableBitmap(BackgroundRemovalService.Defringe(GetEditableBitmap()!, amount: 55));
        RecordHistory("테두리 완화");
        SetStatus("반투명 테두리 색을 완화했습니다.");
    }

    private void Trim_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureBitmap())
        {
            return;
        }

        PushUndo();
        Int32Rect trimRect = BitmapEditor.GetTransparentBounds(_currentBitmap!);
        TransformAllLayers(bitmap => BitmapEditor.Crop(bitmap, trimRect));
        if (_restoreSourceBitmap is not null)
        {
            _restoreSourceBitmap = BitmapEditor.Crop(_restoreSourceBitmap, trimRect);
        }

        RecordHistory("투명 여백 자르기");
        SetStatus("투명 여백을 잘랐습니다.");
    }

    private void Eraser_Click(object sender, RoutedEventArgs e)
    {
        _eraseMode = !_eraseMode;
        _restoreMode = false;
        _autoRestoreMode = false;
        _maskHideMode = false;
        _maskRevealMode = false;
        _pickChroma = false;
        UpdateBrushState();
    }

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        _restoreMode = !_restoreMode;
        _eraseMode = false;
        _autoRestoreMode = false;
        _maskHideMode = false;
        _maskRevealMode = false;
        _pickChroma = false;
        UpdateBrushState();
    }

    private void AutoRestore_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureBitmap())
        {
            return;
        }

        _autoRestoreMode = !_autoRestoreMode;
        _eraseMode = false;
        _restoreMode = false;
        _maskHideMode = false;
        _maskRevealMode = false;
        _pickChroma = false;

        if (_autoRestoreMode && !CanUseAutoRestoreBrush())
        {
            _autoRestoreMode = false;
            UpdateBrushState();
            SetStatus("자동 복원은 원본과 현재 이미지 크기가 같을 때만 사용할 수 있습니다.");
            return;
        }

        UpdateBrushState();
    }

    private void RotateLeft_Click(object sender, RoutedEventArgs e)
    {
        TransformCurrent(bitmap => BitmapEditor.Rotate(bitmap, -90), "왼쪽으로 회전했습니다.");
    }

    private void RotateRight_Click(object sender, RoutedEventArgs e)
    {
        TransformCurrent(bitmap => BitmapEditor.Rotate(bitmap, 90), "오른쪽으로 회전했습니다.");
    }

    private void FlipHorizontal_Click(object sender, RoutedEventArgs e)
    {
        TransformCurrent(BitmapEditor.FlipHorizontal, "좌우 반전했습니다.");
    }

    private void FlipVertical_Click(object sender, RoutedEventArgs e)
    {
        TransformCurrent(BitmapEditor.FlipVertical, "상하 반전했습니다.");
    }

    private void CropMode_Click(object sender, RoutedEventArgs e)
    {
        _cropMode = !_cropMode;
        _eraseMode = false;
        _restoreMode = false;
        _autoRestoreMode = false;
        _maskHideMode = false;
        _maskRevealMode = false;
        _pickChroma = false;
        CropButton.Background = _cropMode ? FindBrush("AccentBrush") : FindBrush("PanelLiftBrush");
        UpdateBrushButtons();
        SetStatus(_cropMode ? "캔버스에서 자를 영역을 드래그하세요." : "자르기 선택 해제");
    }

    private void ApplyCrop_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureBitmap() || CropRect.Visibility != Visibility.Visible)
        {
            return;
        }

        PushUndo();
        double left = Canvas.GetLeft(CropRect);
        double top = Canvas.GetTop(CropRect);
        var rect = new Int32Rect(
            (int)Math.Round(left),
            (int)Math.Round(top),
            (int)Math.Round(CropRect.Width),
            (int)Math.Round(CropRect.Height));

        TransformAllLayers(bitmap => BitmapEditor.Crop(bitmap, rect));
        if (_restoreSourceBitmap is not null)
        {
            _restoreSourceBitmap = BitmapEditor.Crop(_restoreSourceBitmap, rect);
        }

        _cropMode = false;
        CropRect.Visibility = Visibility.Collapsed;
        RecordHistory("자르기");
        SetStatus("선택 영역으로 잘랐습니다.");
    }

    private void ResizeApply_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureBitmap())
        {
            return;
        }

        int longSide = int.TryParse(ResizeBox.Text, out int parsed)
            ? parsed
            : (int)ResizeSlider.Value;
        longSide = Math.Clamp(longSide, 64, 16384);

        double ratio = _currentBitmap!.PixelWidth >= _currentBitmap.PixelHeight
            ? (double)longSide / _currentBitmap.PixelWidth
            : (double)longSide / _currentBitmap.PixelHeight;

        PushUndo();
        int width = Math.Max(1, (int)Math.Round(_currentBitmap.PixelWidth * ratio));
        int height = Math.Max(1, (int)Math.Round(_currentBitmap.PixelHeight * ratio));
        TransformAllLayers(bitmap => BitmapEditor.Resize(bitmap, width, height));
        if (_restoreSourceBitmap is not null)
        {
            _restoreSourceBitmap = BitmapEditor.Resize(_restoreSourceBitmap, width, height);
        }

        RecordHistory($"리사이즈 {width} x {height}");
        SetStatus($"리사이즈 완료: {width} x {height}");
    }

    private void ApplyAdjustments_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureBitmap())
        {
            return;
        }

        var settings = new AdjustmentSettings(
            BrightnessSlider.Value,
            ContrastSlider.Value,
            SaturationSlider.Value,
            DenoiseSlider.Value,
            SharpenSlider.Value);

        PushUndo();
        SetEditableBitmap(BitmapEditor.ApplyAdjustments(GetEditableBitmap()!, settings));
        if (_restoreSourceBitmap is not null)
        {
            _restoreSourceBitmap = BitmapEditor.ApplyAdjustments(_restoreSourceBitmap, settings);
        }

        RecordHistory("색감 보정");
        SetStatus("보정을 적용했습니다.");
    }

    private void AutoEnhance_Click(object sender, RoutedEventArgs e)
    {
        TransformCurrent(BitmapEditor.AutoEnhance, "자동 보정을 적용했습니다.");
    }

    private void ResetEdits_Click(object sender, RoutedEventArgs e)
    {
        if (_originalBitmap is null)
        {
            return;
        }

        PushUndo();
        _currentBitmap = _originalBitmap;
        _restoreSourceBitmap = _originalBitmap;
        ClearLayers();
        var layer = new ImageLayer("배경", _originalBitmap);
        AddLayer(layer);
        SelectLayer(layer);
        ResetInteractionModes();
        UpdatePreview(_currentBitmap, _currentPath);
        RecordHistory("원본 복원");
        SetStatus("원본으로 되돌렸습니다.");
    }

    private void SaveCurrent_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureBitmap())
        {
            return;
        }

        string initialName = _currentPath is null
            ? "crita2x.png"
            : $"{Path.GetFileNameWithoutExtension(_currentPath)}_edit.png";
        var dialog = new SaveFileDialog
        {
            Title = "현재 이미지 저장",
            FileName = initialName,
            Filter = "PNG|*.png|JPEG|*.jpg|TIFF|*.tif|BMP|*.bmp"
        };

        if (dialog.ShowDialog(this) == true)
        {
            BitmapEditor.Save(_currentBitmap!, dialog.FileName);
            SetLastOutputPath(dialog.FileName);
            SetStatus($"저장 완료: {dialog.FileName}");
        }
    }

    private void OpenOutputFolder_Click(object sender, RoutedEventArgs e)
    {
        OpenFolder(OutputFolderBox.Text);
    }

    private void AddBlankLayer_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureBitmap())
        {
            return;
        }

        PushUndo();
        int width = _currentBitmap!.PixelWidth;
        int height = _currentBitmap.PixelHeight;
        var layer = new ImageLayer($"레이어 {_layers.Count + 1}", BitmapEditor.CreateTransparent(width, height, _currentBitmap.DpiX, _currentBitmap.DpiY));
        AddLayer(layer, Math.Clamp(LayerList.SelectedIndex + 1, 0, _layers.Count));
        SelectLayer(layer);
        RefreshCompositeFromLayers();
        RecordHistory("빈 레이어 추가");
        SetStatus("빈 레이어를 추가했습니다.");
    }

    private void DuplicateLayer_Click(object sender, RoutedEventArgs e)
    {
        if (GetActiveLayer() is not ImageLayer activeLayer)
        {
            SetStatus("복제할 레이어가 없습니다.");
            return;
        }

        PushUndo();
        var layer = new ImageLayer($"{activeLayer.Name} 복사", activeLayer.Bitmap)
        {
            Mask = activeLayer.Mask,
            IsVisible = activeLayer.IsVisible,
            Opacity = activeLayer.Opacity,
            BlendMode = activeLayer.BlendMode
        };
        AddLayer(layer, Math.Clamp(LayerList.SelectedIndex + 1, 0, _layers.Count));
        SelectLayer(layer);
        RefreshCompositeFromLayers();
        RecordHistory("레이어 복제");
        SetStatus("선택 레이어를 복제했습니다.");
    }

    private void DeleteLayer_Click(object sender, RoutedEventArgs e)
    {
        if (GetActiveLayer() is not ImageLayer activeLayer)
        {
            return;
        }

        if (_layers.Count <= 1)
        {
            SetStatus("레이어는 최소 1개가 필요합니다.");
            return;
        }

        PushUndo();
        int index = LayerList.SelectedIndex;
        RemoveLayer(activeLayer);
        LayerList.SelectedIndex = Math.Clamp(index, 0, _layers.Count - 1);
        RefreshCompositeFromLayers();
        RecordHistory("레이어 삭제");
        SetStatus("선택 레이어를 삭제했습니다.");
    }

    private void MergeVisibleLayers_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureBitmap() || _layers.Count == 0)
        {
            return;
        }

        PushUndo();
        BitmapSource merged = CompositeVisibleLayers();
        ClearLayers();
        var layer = new ImageLayer("병합 레이어", merged);
        AddLayer(layer);
        SelectLayer(layer);
        _currentBitmap = merged;
        _restoreSourceBitmap = merged;
        UpdatePreview(_currentBitmap, _currentPath);
        RecordHistory("레이어 병합");
        SetStatus("보이는 레이어를 병합했습니다.");
    }

    private void AddLayerMask_Click(object sender, RoutedEventArgs e)
    {
        if (GetActiveLayer() is not ImageLayer activeLayer)
        {
            SetStatus("마스크를 추가할 레이어가 없습니다.");
            return;
        }

        PushUndo();
        SetLayerMask(activeLayer, BitmapEditor.CreateMask(activeLayer.Bitmap.PixelWidth, activeLayer.Bitmap.PixelHeight, 255, activeLayer.Bitmap.DpiX, activeLayer.Bitmap.DpiY));
        RefreshCompositeFromLayers();
        RecordHistory("레이어 마스크 추가");
        SetStatus("선택 레이어에 흰색 마스크를 추가했습니다.");
    }

    private void RemoveLayerMask_Click(object sender, RoutedEventArgs e)
    {
        if (GetActiveLayer() is not ImageLayer activeLayer || activeLayer.Mask is null)
        {
            SetStatus("삭제할 레이어 마스크가 없습니다.");
            return;
        }

        PushUndo();
        SetLayerMask(activeLayer, null);
        RefreshCompositeFromLayers();
        RecordHistory("레이어 마스크 삭제");
        SetStatus("선택 레이어의 마스크를 삭제했습니다.");
    }

    private void InvertLayerMask_Click(object sender, RoutedEventArgs e)
    {
        if (GetActiveLayer() is not ImageLayer activeLayer || activeLayer.Mask is null)
        {
            SetStatus("반전할 레이어 마스크가 없습니다.");
            return;
        }

        PushUndo();
        SetLayerMask(activeLayer, BitmapEditor.InvertMask(activeLayer.Mask));
        RefreshCompositeFromLayers();
        RecordHistory("레이어 마스크 반전");
        SetStatus("선택 레이어의 마스크를 반전했습니다.");
    }

    private void MaskHideBrush_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureMaskBrush())
        {
            return;
        }

        _maskHideMode = !_maskHideMode;
        _maskRevealMode = false;
        _eraseMode = false;
        _restoreMode = false;
        _autoRestoreMode = false;
        _pickChroma = false;
        UpdateBrushState();
    }

    private void MaskRevealBrush_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureMaskBrush())
        {
            return;
        }

        _maskRevealMode = !_maskRevealMode;
        _maskHideMode = false;
        _eraseMode = false;
        _restoreMode = false;
        _autoRestoreMode = false;
        _pickChroma = false;
        UpdateBrushState();
    }

    private void LayerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressLayerSelection)
        {
            return;
        }

        UpdateLayerControls();
    }

    private void LayerVisibility_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressLayerRefresh)
        {
            return;
        }

        RefreshCompositeFromLayers();
        _layerVisibilityEditCaptured = false;
        RecordHistory("레이어 표시 변경");
        SetStatus("레이어 표시 상태를 변경했습니다.");
    }

    private void LayerVisibility_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_layerVisibilityEditCaptured || GetActiveLayer() is null)
        {
            return;
        }

        PushUndo();
        _layerVisibilityEditCaptured = true;
    }

    private void LayerOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressLayerControlUpdate || GetActiveLayer() is not ImageLayer activeLayer)
        {
            return;
        }

        _suppressLayerRefresh = true;
        try
        {
            activeLayer.Opacity = LayerOpacitySlider.Value;
        }
        finally
        {
            _suppressLayerRefresh = false;
        }

        LayerOpacityText.Text = $"{activeLayer.Opacity:F0}%";
        RefreshCompositeFromLayers();
    }

    private void LayerOpacitySlider_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (GetActiveLayer() is null)
        {
            return;
        }

        PushUndo();
        _layerOpacityEditCaptured = true;
    }

    private void LayerOpacitySlider_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_layerOpacityEditCaptured)
        {
            return;
        }

        _layerOpacityEditCaptured = false;
        RecordHistory("레이어 불투명도 변경");
        SetStatus("레이어 불투명도를 변경했습니다.");
    }

    private void LayerBlendModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressLayerControlUpdate || GetActiveLayer() is not ImageLayer activeLayer)
        {
            return;
        }

        ImageBlendMode blendMode = ParseBlendMode(GetComboTag(LayerBlendModeBox, nameof(ImageBlendMode.Normal)));
        if (activeLayer.BlendMode == blendMode)
        {
            return;
        }

        PushUndo();
        activeLayer.BlendMode = blendMode;
        RefreshCompositeFromLayers();
        RecordHistory($"블렌드 모드: {activeLayer.BlendModeText}");
        SetStatus($"블렌드 모드 변경: {activeLayer.BlendModeText}");
    }

    private void RenameLayer_Click(object sender, RoutedEventArgs e)
    {
        if (GetActiveLayer() is not ImageLayer activeLayer)
        {
            return;
        }

        string name = string.IsNullOrWhiteSpace(LayerNameBox.Text)
            ? activeLayer.Name
            : LayerNameBox.Text.Trim();

        if (string.Equals(activeLayer.Name, name, StringComparison.Ordinal))
        {
            return;
        }

        PushUndo();
        activeLayer.Name = name;
        UpdateLayerControls();
        RecordHistory("레이어 이름 변경");
        SetStatus($"레이어 이름 변경: {name}");
    }

    private void ResetAdvancedSettings_Click(object sender, RoutedEventArgs e)
    {
        PresetBox.SelectedIndex = 0;
        AutoTileBox.IsChecked = true;
        LoadThreadsBox.Text = "1";
        ProcThreadsBox.Text = "2";
        SaveThreadsBox.Text = "2";
        OutputLocationBox.SelectedIndex = 0;
        OutputSuffixBox.Text = "_crita2x";
        AutoPreviewOutputBox.IsChecked = true;
        ContinueOnErrorBox.IsChecked = true;
        OpenAfterRunBox.IsChecked = false;
        CompletionSoundBox.IsChecked = false;
        SetStatus("세부 설정을 초기화했습니다.");
    }

    private void ApplyPreset_Click(object sender, RoutedEventArgs e)
    {
        switch (GetComboTag(PresetBox, "fast"))
        {
            case "anime":
                SetComboByTag(ModelBox, "models-cunet");
                SetComboByTag(ScaleBox, "2");
                SetComboByTag(NoiseBox, "1");
                SetComboByTag(FormatBox, "png");
                AutoTileBox.IsChecked = true;
                TtaBox.IsChecked = true;
                LoadThreadsBox.Text = "1";
                ProcThreadsBox.Text = "2";
                SaveThreadsBox.Text = "2";
                OutputSuffixBox.Text = "_crita2x_anime_{scale}x";
                SetStatus("프리셋 적용: 애니 디테일");
                break;

            case "photo":
                SetComboByTag(ModelBox, "models-upconv_7_photo");
                SetComboByTag(ScaleBox, "2");
                SetComboByTag(NoiseBox, "1");
                SetComboByTag(FormatBox, "original");
                AutoTileBox.IsChecked = true;
                TtaBox.IsChecked = false;
                LoadThreadsBox.Text = "1";
                ProcThreadsBox.Text = "2";
                SaveThreadsBox.Text = "2";
                OutputSuffixBox.Text = "_crita2x_photo_{scale}x";
                SetStatus("프리셋 적용: 사진 클린");
                break;

            case "max":
                SetComboByTag(ModelBox, "models-cunet");
                SetComboByTag(ScaleBox, "4");
                SetComboByTag(NoiseBox, "2");
                SetComboByTag(FormatBox, "png");
                AutoTileBox.IsChecked = false;
                TileSlider.Value = 512;
                TtaBox.IsChecked = true;
                LoadThreadsBox.Text = "1";
                ProcThreadsBox.Text = "4";
                SaveThreadsBox.Text = "2";
                OutputSuffixBox.Text = "_crita2x_max_{scale}x";
                SetStatus("프리셋 적용: 최대 품질");
                break;

            default:
                SetComboByTag(ModelBox, "models-cunet");
                SetComboByTag(ScaleBox, "2");
                SetComboByTag(NoiseBox, "0");
                SetComboByTag(FormatBox, "png");
                AutoTileBox.IsChecked = true;
                TtaBox.IsChecked = false;
                LoadThreadsBox.Text = "1";
                ProcThreadsBox.Text = "2";
                SaveThreadsBox.Text = "2";
                OutputSuffixBox.Text = "_crita2x_fast_{scale}x";
                SetStatus("프리셋 적용: 빠른 확인");
                break;
        }
    }

    private static void OpenFolder(string folder)
    {
        if (!Directory.Exists(folder))
        {
            Directory.CreateDirectory(folder);
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{folder}\"",
            UseShellExecute = true
        });
    }

    private static void RevealFileInExplorer(string path)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{path}\"",
            UseShellExecute = true
        });
    }

    private static bool TryGetJobFromSender(object sender, out BatchJob job)
    {
        if (sender is FrameworkElement { DataContext: BatchJob batchJob })
        {
            job = batchJob;
            return true;
        }

        job = null!;
        return false;
    }

    private static bool TryGetOutputPath(BatchJob job, out string outputPath)
    {
        outputPath = job.OutputPath ?? "";
        return !string.IsNullOrWhiteSpace(outputPath) && File.Exists(outputPath);
    }

    private bool TryGetLastOutputPath(out string outputPath)
    {
        outputPath = _lastOutputPath ?? "";
        if (!string.IsNullOrWhiteSpace(outputPath) && File.Exists(outputPath))
        {
            return true;
        }

        SetLastOutputPath(null);
        return false;
    }

    private void SetLastOutputPath(string? path)
    {
        _lastOutputPath = !string.IsNullOrWhiteSpace(path) && File.Exists(path)
            ? path
            : null;
        UpdateLastOutputActions();
    }

    private void UpdateLastOutputActions()
    {
        bool hasOutput = !string.IsNullOrWhiteSpace(_lastOutputPath) && File.Exists(_lastOutputPath);
        if (!hasOutput)
        {
            _lastOutputPath = null;
        }

        OpenLastOutputButton.IsEnabled = hasOutput;
        RevealLastOutputButton.IsEnabled = hasOutput;
        LastOutputNameText.Text = hasOutput
            ? Path.GetFileName(_lastOutputPath!)
            : "완료 결과 없음";
    }

    private void SelectJobByOutput(string outputPath)
    {
        BatchJob? job = _jobs.FirstOrDefault(job =>
            job.OutputPath is string path
            && string.Equals(Path.GetFullPath(path), Path.GetFullPath(outputPath), StringComparison.OrdinalIgnoreCase));
        if (job is not null && !ReferenceEquals(JobList.SelectedItem, job))
        {
            JobList.SelectedItem = job;
            JobList.ScrollIntoView(job);
        }
    }

    private void Preview_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_currentBitmap is null || !Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            return;
        }

        e.Handled = true;
        Point imagePoint = e.GetPosition(ImageHost);
        Point viewportPoint = e.GetPosition(PreviewScroller);
        double factor = e.Delta > 0 ? 1.15 : 1 / 1.15;
        SetZoom(_zoom * factor, imagePoint, viewportPoint);
    }

    private void ZoomOut_Click(object sender, RoutedEventArgs e)
    {
        SetZoom(_zoom / 1.2);
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e)
    {
        SetZoom(_zoom * 1.2);
    }

    private void ZoomActual_Click(object sender, RoutedEventArgs e)
    {
        SetZoom(1.0);
    }

    private void ZoomFit_Click(object sender, RoutedEventArgs e)
    {
        FitImageToView();
    }

    private void SetCompareBaseline_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureBitmap())
        {
            return;
        }

        _compareBaselineBitmap = _currentBitmap;
        UpdateCompareView();
        SetStatus("현재 상태를 비교 기준으로 저장했습니다.");
    }

    private void CompareToggle_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureBitmap())
        {
            return;
        }

        _compareBaselineBitmap ??= _originalBitmap ?? _currentBitmap;
        _compareMode = !_compareMode;
        UpdateCompareView();
        SetStatus(_compareMode ? "비교 보기 켜짐" : "비교 보기 꺼짐");
    }

    private void CompareSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateCompareOverlay();
    }

    private void SetZoom(double zoom)
    {
        SetZoom(zoom, null, null);
    }

    private void SetZoom(double zoom, Point? imagePoint, Point? viewportPoint)
    {
        if (_currentBitmap is null)
        {
            return;
        }

        double newZoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        if (Math.Abs(newZoom - _zoom) < 0.0001)
        {
            return;
        }

        _zoom = newZoom;
        ApplyZoom();

        if (imagePoint is { } anchor && viewportPoint is { } viewport)
        {
            ImageHost.UpdateLayout();
            PreviewScroller.UpdateLayout();
            PreviewScroller.ScrollToHorizontalOffset((anchor.X * _zoom) - viewport.X);
            PreviewScroller.ScrollToVerticalOffset((anchor.Y * _zoom) - viewport.Y);
        }
    }

    private void ApplyZoom()
    {
        PreviewScaleTransform.ScaleX = _zoom;
        PreviewScaleTransform.ScaleY = _zoom;
        UpdateZoomText();
    }

    private void UpdateZoomText()
    {
        ZoomText.Text = $"{Math.Round(_zoom * 100)}%";
    }

    private void FitImageToView()
    {
        if (_currentBitmap is null || PreviewScroller.ViewportWidth <= 0 || PreviewScroller.ViewportHeight <= 0)
        {
            return;
        }

        double availableWidth = Math.Max(1, PreviewScroller.ViewportWidth - 24);
        double availableHeight = Math.Max(1, PreviewScroller.ViewportHeight - 24);
        double fitZoom = Math.Min(availableWidth / _currentBitmap.PixelWidth, availableHeight / _currentBitmap.PixelHeight);
        _zoom = Math.Clamp(Math.Min(1.0, fitZoom), MinZoom, MaxZoom);
        ApplyZoom();
        ImageHost.UpdateLayout();
        PreviewScroller.UpdateLayout();
        PreviewScroller.ScrollToHorizontalOffset(Math.Max(0, (ImageHost.ActualWidth * _zoom - PreviewScroller.ViewportWidth) / 2));
        PreviewScroller.ScrollToVerticalOffset(Math.Max(0, (ImageHost.ActualHeight * _zoom - PreviewScroller.ViewportHeight) / 2));
    }

    private void Preview_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!EnsureBitmap() || !TryGetImagePoint(e.GetPosition(ImageHost), out Point imagePoint))
        {
            return;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            _isPanning = true;
            _panStart = e.GetPosition(PreviewScroller);
            _panStartHorizontalOffset = PreviewScroller.HorizontalOffset;
            _panStartVerticalOffset = PreviewScroller.VerticalOffset;
            ImageHost.Cursor = Cursors.SizeAll;
            Mouse.Capture(ImageHost);
            e.Handled = true;
            return;
        }

        if (_pickChroma)
        {
            _chromaColor = SampleColor(_currentBitmap!, (int)imagePoint.X, (int)imagePoint.Y);
            _pickChroma = false;
            SetStatus($"색상 선택: RGB({_chromaColor.R}, {_chromaColor.G}, {_chromaColor.B})");
            return;
        }

        if (IsBrushModeActive())
        {
            Mouse.Capture(ImageHost);
            ApplyBrush(imagePoint);
            return;
        }

        if (_cropMode)
        {
            _isCropping = true;
            _cropStart = imagePoint;
            Mouse.Capture(ImageHost);
            CropRect.Visibility = Visibility.Visible;
            Canvas.SetLeft(CropRect, imagePoint.X);
            Canvas.SetTop(CropRect, imagePoint.Y);
            CropRect.Width = 1;
            CropRect.Height = 1;
        }
    }

    private void Preview_MouseMove(object sender, MouseEventArgs e)
    {
        if (_isPanning && e.LeftButton == MouseButtonState.Pressed)
        {
            Point current = e.GetPosition(PreviewScroller);
            PreviewScroller.ScrollToHorizontalOffset(_panStartHorizontalOffset - (current.X - _panStart.X));
            PreviewScroller.ScrollToVerticalOffset(_panStartVerticalOffset - (current.Y - _panStart.Y));
            e.Handled = true;
            return;
        }

        Point point = e.GetPosition(ImageHost);
        if (TryGetImagePoint(point, out Point imagePoint))
        {
            UpdateBrushGhost(imagePoint);
        }

        if (_isCropping && e.LeftButton == MouseButtonState.Pressed && TryGetImagePoint(point, out imagePoint))
        {
            double x = Math.Min(_cropStart.X, imagePoint.X);
            double y = Math.Min(_cropStart.Y, imagePoint.Y);
            double width = Math.Abs(_cropStart.X - imagePoint.X);
            double height = Math.Abs(_cropStart.Y - imagePoint.Y);
            Canvas.SetLeft(CropRect, x);
            Canvas.SetTop(CropRect, y);
            CropRect.Width = Math.Max(1, width);
            CropRect.Height = Math.Max(1, height);
        }
        else if (IsBrushModeActive() && e.LeftButton == MouseButtonState.Pressed && TryGetImagePoint(point, out imagePoint))
        {
            ApplyBrush(imagePoint);
        }
    }

    private void Preview_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_brushHistoryCaptured)
        {
            RecordHistory(GetBrushHistoryLabel());
        }

        _isCropping = false;
        _isPanning = false;
        _brushHistoryCaptured = false;
        _lastBrushPoint = null;
        ImageHost.Cursor = null;
        Mouse.Capture(null);
    }

    private void Preview_MouseLeave(object sender, MouseEventArgs e)
    {
        BrushGhost.Visibility = Visibility.Collapsed;
    }

    private void AddImages(IEnumerable<string> paths)
    {
        var imagePaths = ExpandPaths(paths)
            .Where(path => _supportedExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (string path in imagePaths)
        {
            if (_jobs.Any(job => string.Equals(job.FilePath, path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            _jobs.Add(new BatchJob(path));
        }

        UpdateQueueText();

        if (_jobs.Count > 0 && JobList.SelectedItem is null)
        {
            JobList.SelectedIndex = 0;
        }

        SetStatus($"{imagePaths.Length}개 이미지를 추가했습니다.");
    }

    private async Task RunJobsAsync(IReadOnlyCollection<BatchJob> jobs)
    {
        if (jobs.Count == 0)
        {
            SetStatus("실행할 이미지가 없습니다.");
            return;
        }

        var options = BuildWaifu2xOptions();
        if (!File.Exists(options.EnginePath))
        {
            EnginePathBox.Text = Waifu2xService.FindDefaultEngine();
            RefreshModelChoices();
            options = BuildWaifu2xOptions();
        }

        if (!File.Exists(options.EnginePath))
        {
            SetStatus("waifu2x-ncnn-vulkan.exe 경로가 필요합니다.");
            MessageBox.Show(this, "Waifu2x 엔진 경로를 먼저 선택하세요.", "엔진 필요", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _queueCancellation?.Cancel();
        _queueCancellation = new CancellationTokenSource();
        string lastOutputFolder = OutputFolderBox.Text;
        int completedCount = 0;
        int failedCount = 0;

        try
        {
            foreach (BatchJob job in jobs)
            {
                _queueCancellation.Token.ThrowIfCancellationRequested();
                job.IsRunning = true;
                job.Progress = 0;
                job.OutputPath = null;
                job.Status = "실행 중";
                SetStatus($"업스케일 중: {job.FileName}");

                string outputFolder = GetOutputFolderForJob(job);
                lastOutputFolder = outputFolder;

                try
                {
                    var progress = new Progress<int>(value => job.Progress = value);
                    string outputPath = await Waifu2xService.RunAsync(
                        job.FilePath,
                        outputFolder,
                        options,
                        progress,
                        _queueCancellation.Token);

                    job.OutputPath = outputPath;
                    SetLastOutputPath(outputPath);
                    job.Progress = 100;
                    job.Status = "완료";
                    job.IsRunning = false;
                    completedCount++;

                    if (AutoPreviewOutputBox.IsChecked == true && ReferenceEquals(JobList.SelectedItem, job))
                    {
                        LoadImage(outputPath);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ContinueOnErrorBox.IsChecked == true)
                {
                    job.Status = "오류";
                    job.IsRunning = false;
                    failedCount++;
                    SetStatus($"{job.FileName} 실패: {ex.Message}");
                }
            }

            SetStatus(failedCount > 0
                ? $"업스케일 완료: {completedCount}개 완료, {failedCount}개 오류"
                : "업스케일 완료");
            if (OpenAfterRunBox.IsChecked == true)
            {
                OpenFolder(lastOutputFolder);
            }

            if (CompletionSoundBox.IsChecked == true)
            {
                System.Media.SystemSounds.Asterisk.Play();
            }
        }
        catch (OperationCanceledException)
        {
            foreach (BatchJob job in jobs.Where(job => job.IsRunning))
            {
                job.Status = "중지됨";
                job.IsRunning = false;
            }

            SetStatus("실행이 중지되었습니다.");
        }
        catch (Exception ex)
        {
            foreach (BatchJob job in jobs.Where(job => job.IsRunning))
            {
                job.Status = "오류";
                job.IsRunning = false;
            }

            SetStatus(ex.Message);
            MessageBox.Show(this, ex.Message, "실행 오류", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private Waifu2xOptions BuildWaifu2xOptions()
    {
        return new Waifu2xOptions(
            EnginePathBox.Text.Trim(),
            GetComboTag(ModelBox, "models-cunet"),
            GetComboInt(ScaleBox, 2),
            GetComboInt(NoiseBox, 1),
            AutoTileBox.IsChecked == true ? 0 : (int)TileSlider.Value,
            GetComboTag(GpuBox, "auto"),
            GetPositiveInt(LoadThreadsBox.Text, 1, 1, 16),
            GetPositiveInt(ProcThreadsBox.Text, 2, 1, 16),
            GetPositiveInt(SaveThreadsBox.Text, 2, 1, 16),
            TtaBox.IsChecked == true,
            GetComboTag(FormatBox, "png"),
            OutputSuffixBox.Text);
    }

    private string GetOutputFolderForJob(BatchJob job)
    {
        string sourceFolder = Path.GetDirectoryName(job.FilePath) ?? OutputFolderBox.Text;
        return GetComboTag(OutputLocationBox, "fixed") switch
        {
            "source" => sourceFolder,
            "source-subfolder" => Path.Combine(sourceFolder, "Crita2x"),
            _ => OutputFolderBox.Text
        };
    }

    private void RefreshModelChoices(bool showStatus = false)
    {
        string selectedModel = GetComboTag(ModelBox, "models-cunet");
        ModelBox.Items.Clear();

        foreach (Waifu2xModelOption model in Waifu2xService.DiscoverModels(EnginePathBox.Text.Trim()))
        {
            AddModelChoice(model, select: false);
        }

        SetComboByTag(ModelBox, selectedModel);
        if (ModelBox.SelectedIndex < 0)
        {
            SetComboByTag(ModelBox, "models-cunet");
        }

        if (ModelBox.SelectedIndex < 0 && ModelBox.Items.Count > 0)
        {
            ModelBox.SelectedIndex = 0;
        }

        if (showStatus)
        {
            SetStatus($"{ModelBox.Items.Count}개 모델을 불러왔습니다.");
        }

        UpdateModelInfo();
    }

    private void AddModelChoice(Waifu2xModelOption model, bool select)
    {
        for (int i = 0; i < ModelBox.Items.Count; i++)
        {
            if (ModelBox.Items[i] is ComboBoxItem item &&
                string.Equals(item.Tag?.ToString(), model.ModelPath, StringComparison.OrdinalIgnoreCase))
            {
                if (select)
                {
                    ModelBox.SelectedIndex = i;
                }

                return;
            }
        }

        var itemToAdd = new ComboBoxItem
        {
            Content = model.DisplayName,
            Tag = model.ModelPath
        };
        ModelBox.Items.Add(itemToAdd);
        if (select)
        {
            ModelBox.SelectedItem = itemToAdd;
        }
    }

    private void UpdateModelInfo()
    {
        if (ModelInfoText is null)
        {
            return;
        }

        string model = GetComboTag(ModelBox, string.Empty);
        if (string.IsNullOrWhiteSpace(model))
        {
            ModelInfoText.Text = "사용 가능한 모델이 없습니다.";
            return;
        }

        string engineDirectory = Path.GetDirectoryName(EnginePathBox.Text.Trim()) ?? string.Empty;
        string resolvedPath = Path.IsPathRooted(model)
            ? model
            : Path.Combine(engineDirectory, model);
        string name = Path.GetFileName(resolvedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        ModelInfoText.Text = Waifu2xService.IsModelDirectory(resolvedPath)
            ? $"{name} · 모델 파일 확인됨"
            : $"{model} · 실행 시 엔진에서 확인";
    }

    private async Task LoadGpuChoicesAsync()
    {
        string[] gpuNames = await Task.Run(QueryVideoControllerNames);

        GpuBox.Items.Clear();
        GpuBox.Items.Add(new ComboBoxItem { Content = "자동", Tag = "auto" });
        GpuBox.Items.Add(new ComboBoxItem { Content = "CPU", Tag = "-1" });

        for (int i = 0; i < gpuNames.Length; i++)
        {
            GpuBox.Items.Add(new ComboBoxItem
            {
                Content = $"{i} - {gpuNames[i]}",
                Tag = i.ToString()
            });
        }

        if (gpuNames.Length == 0)
        {
            GpuBox.Items.Add(new ComboBoxItem { Content = "0 - GPU 0", Tag = "0" });
        }

        GpuBox.SelectedIndex = 0;
    }

    private static string[] QueryVideoControllerNames()
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -Command \"Get-CimInstance Win32_VideoController | ForEach-Object { $_.Name }\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            process.Start();
            string output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(3000))
            {
                process.Kill(entireProcessTree: true);
            }

            return output
                .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private void LoadImage(string path)
    {
        try
        {
            BitmapSource image = BitmapEditor.LoadBitmap(path);
            _originalBitmap = image;
            _restoreSourceBitmap = image;
            _compareBaselineBitmap = image;
            _currentBitmap = image;
            _currentPath = path;
            _compareMode = false;
            ClearLayers();
            var layer = new ImageLayer("배경", image);
            AddLayer(layer);
            SelectLayer(layer);
            ClearHistory();
            ResetInteractionModes();
            _zoom = 1.0;
            UpdatePreview(image, path);
            RecordHistory("불러오기");
            Dispatcher.BeginInvoke(new Action(FitImageToView));
            SetStatus($"불러옴: {Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
            MessageBox.Show(this, ex.Message, "이미지 열기 오류", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UpdatePreview(BitmapSource? bitmap, string? path)
    {
        PreviewImage.Source = bitmap;
        EmptyState.Visibility = bitmap is null ? Visibility.Visible : Visibility.Collapsed;
        CropRect.Visibility = Visibility.Collapsed;

        if (bitmap is null)
        {
            ImageHost.Width = double.NaN;
            ImageHost.Height = double.NaN;
            OverlayCanvas.Width = 0;
            OverlayCanvas.Height = 0;
            _zoom = 1.0;
            ApplyZoom();
            CurrentFileText.Text = "준비됨";
            ImageInfoText.Text = "";
            CurrentDetailText.Text = "선택된 이미지 없음";
            UpdateCompareView();
            return;
        }

        ImageHost.Width = bitmap.PixelWidth;
        ImageHost.Height = bitmap.PixelHeight;
        OverlayCanvas.Width = bitmap.PixelWidth;
        OverlayCanvas.Height = bitmap.PixelHeight;

        CurrentFileText.Text = path is null ? "편집 중" : path;
        ImageInfoText.Text = $"{bitmap.PixelWidth} x {bitmap.PixelHeight}";
        CurrentDetailText.Text = $"{Path.GetFileName(path ?? "편집 이미지")}\n{bitmap.PixelWidth} x {bitmap.PixelHeight}\n{bitmap.Format}";
        ApplyZoom();
        UpdateCompareView();
    }

    private void UpdateCompareView()
    {
        bool hasImage = _currentBitmap is not null;
        bool canCompare = hasImage && _compareBaselineBitmap is not null;
        bool showCompare = _compareMode && canCompare;

        CompareBaselineButton.IsEnabled = hasImage;
        CompareToggleButton.IsEnabled = canCompare;
        CompareSlider.IsEnabled = canCompare;
        CompareImage.Source = showCompare ? _compareBaselineBitmap : null;
        CompareImage.Visibility = showCompare ? Visibility.Visible : Visibility.Collapsed;
        CompareOverlay.Visibility = showCompare ? Visibility.Visible : Visibility.Collapsed;
        CompareToggleButton.Background = showCompare ? FindBrush("AccentBrush") : FindBrush("PanelLiftBrush");
        CompareToggleButton.Foreground = showCompare ? new SolidColorBrush(Color.FromRgb(7, 19, 17)) : FindBrush("InkBrush");

        UpdateCompareOverlay();
    }

    private void UpdateCompareOverlay()
    {
        if (_currentBitmap is null || CompareClip is null)
        {
            return;
        }

        double width = _currentBitmap.PixelWidth;
        double height = _currentBitmap.PixelHeight;
        double split = width * Math.Clamp(CompareSlider.Value / 100.0, 0, 1);

        CompareClip.Rect = new Rect(0, 0, split, height);
        CompareOverlay.Width = width;
        CompareOverlay.Height = height;
        CompareDivider.Height = height;
        Canvas.SetLeft(CompareDivider, Math.Clamp(split - 1, 0, Math.Max(0, width - 2)));
        Canvas.SetTop(CompareDivider, 0);
        Canvas.SetLeft(CompareBeforeBadge, 12);
        Canvas.SetTop(CompareBeforeBadge, 12);
        Canvas.SetLeft(CompareAfterBadge, Math.Max(12, width - 58));
        Canvas.SetTop(CompareAfterBadge, 12);
    }

    private void AddLayer(ImageLayer layer, int? index = null)
    {
        layer.PropertyChanged += Layer_PropertyChanged;
        if (index is int insertIndex)
        {
            _layers.Insert(Math.Clamp(insertIndex, 0, _layers.Count), layer);
        }
        else
        {
            _layers.Add(layer);
        }

        UpdateLayerControls();
    }

    private void RemoveLayer(ImageLayer layer)
    {
        layer.PropertyChanged -= Layer_PropertyChanged;
        _layers.Remove(layer);
        UpdateLayerControls();
    }

    private void ClearLayers()
    {
        foreach (ImageLayer layer in _layers)
        {
            layer.PropertyChanged -= Layer_PropertyChanged;
        }

        _layers.Clear();
        UpdateLayerControls();
    }

    private void Layer_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressLayerRefresh)
        {
            return;
        }

        if (e.PropertyName is nameof(ImageLayer.IsVisible) or nameof(ImageLayer.Opacity) or nameof(ImageLayer.Bitmap) or nameof(ImageLayer.BlendMode) or nameof(ImageLayer.Mask))
        {
            RefreshCompositeFromLayers();
            UpdateLayerControls();
            return;
        }

        if (e.PropertyName is nameof(ImageLayer.Name))
        {
            UpdateLayerControls();
        }
    }

    private ImageLayer? GetActiveLayer()
    {
        if (LayerList is null)
        {
            return null;
        }

        if (LayerList.SelectedItem is ImageLayer selectedLayer)
        {
            return selectedLayer;
        }

        return _layers.LastOrDefault();
    }

    private BitmapSource? GetEditableBitmap()
    {
        return GetActiveLayer()?.Bitmap ?? _currentBitmap;
    }

    private bool EnsureMaskBrush()
    {
        if (!EnsureBitmap())
        {
            return false;
        }

        if (GetActiveLayer() is not ImageLayer activeLayer)
        {
            SetStatus("마스크를 칠할 레이어가 없습니다.");
            return false;
        }

        if (activeLayer.Mask is null)
        {
            PushUndo();
            SetLayerMask(activeLayer, BitmapEditor.CreateMask(activeLayer.Bitmap.PixelWidth, activeLayer.Bitmap.PixelHeight, 255, activeLayer.Bitmap.DpiX, activeLayer.Bitmap.DpiY));
            RefreshCompositeFromLayers();
            RecordHistory("레이어 마스크 추가");
        }

        return true;
    }

    private void SetEditableBitmap(BitmapSource bitmap, bool refreshPreview = true)
    {
        if (GetActiveLayer() is ImageLayer activeLayer)
        {
            _suppressLayerRefresh = true;
            try
            {
                activeLayer.Bitmap = bitmap;
            }
            finally
            {
                _suppressLayerRefresh = false;
            }

            if (refreshPreview)
            {
                RefreshCompositeFromLayers();
            }
            else
            {
                UpdateLayerControls();
            }

            return;
        }

        _currentBitmap = bitmap;
        if (refreshPreview)
        {
            UpdatePreview(_currentBitmap, _currentPath);
        }
    }

    private void SetLayerMask(ImageLayer layer, BitmapSource? mask)
    {
        _suppressLayerRefresh = true;
        try
        {
            layer.Mask = mask;
        }
        finally
        {
            _suppressLayerRefresh = false;
        }

        UpdateLayerControls();
    }

    private void TransformAllLayers(Func<BitmapSource, BitmapSource> transform)
    {
        if (_layers.Count == 0)
        {
            if (_currentBitmap is not null)
            {
                _currentBitmap = transform(_currentBitmap);
                UpdatePreview(_currentBitmap, _currentPath);
            }

            return;
        }

        _suppressLayerRefresh = true;
        try
        {
            foreach (ImageLayer layer in _layers)
            {
                layer.Bitmap = transform(layer.Bitmap);
                if (layer.Mask is not null)
                {
                    layer.Mask = transform(layer.Mask);
                }
            }
        }
        finally
        {
            _suppressLayerRefresh = false;
        }

        RefreshCompositeFromLayers();
    }

    private BitmapSource CompositeVisibleLayers()
    {
        if (_layers.Count == 0)
        {
            return _currentBitmap ?? BitmapEditor.CreateTransparent(1, 1);
        }

        int width = Math.Max(1, _layers.Max(layer => layer.Bitmap.PixelWidth));
        int height = Math.Max(1, _layers.Max(layer => layer.Bitmap.PixelHeight));
        BitmapSource first = _layers[0].Bitmap;
        var visibleLayers = _layers
            .Where(layer => layer.IsVisible)
            .Select(layer => (layer.Bitmap, layer.Opacity / 100.0, layer.BlendMode, layer.Mask));

        return BitmapEditor.CompositeLayers(visibleLayers, width, height, first.DpiX, first.DpiY);
    }

    private void RefreshCompositeFromLayers()
    {
        if (_layers.Count == 0)
        {
            UpdateLayerControls();
            return;
        }

        _currentBitmap = CompositeVisibleLayers();
        UpdatePreview(_currentBitmap, _currentPath);
        UpdateLayerControls();
    }

    private IReadOnlyList<ImageLayerSnapshot> CaptureLayers()
    {
        return _layers
            .Select(layer => new ImageLayerSnapshot(layer.Name, layer.Bitmap, layer.Mask, layer.IsVisible, layer.Opacity, layer.BlendMode))
            .ToArray();
    }

    private void RestoreLayers(IReadOnlyList<ImageLayerSnapshot> snapshots, int activeLayerIndex)
    {
        _suppressLayerSelection = true;
        _suppressLayerRefresh = true;
        try
        {
            ClearLayers();
            foreach (ImageLayerSnapshot snapshot in snapshots)
            {
                AddLayer(new ImageLayer(snapshot.Name, snapshot.Bitmap)
                {
                    Mask = snapshot.Mask,
                    IsVisible = snapshot.IsVisible,
                    Opacity = snapshot.Opacity,
                    BlendMode = snapshot.BlendMode
                });
            }

            LayerList.SelectedIndex = _layers.Count == 0
                ? -1
                : Math.Clamp(activeLayerIndex, 0, _layers.Count - 1);
        }
        finally
        {
            _suppressLayerRefresh = false;
            _suppressLayerSelection = false;
        }

        UpdateLayerControls();
    }

    private void SelectLayer(ImageLayer layer)
    {
        _suppressLayerSelection = true;
        try
        {
            LayerList.SelectedItem = layer;
            LayerList.ScrollIntoView(layer);
        }
        finally
        {
            _suppressLayerSelection = false;
        }

        UpdateLayerControls();
    }

    private void UpdateLayerControls()
    {
        if (LayerSummaryText is null)
        {
            return;
        }

        ImageLayer? activeLayer = GetActiveLayer();
        LayerSummaryText.Text = _layers.Count == 0
            ? "열린 이미지 없음"
            : $"{_layers.Count}개 레이어 · 선택 {activeLayer?.Name ?? "-"}";

        _suppressLayerControlUpdate = true;
        try
        {
            LayerOpacitySlider.IsEnabled = activeLayer is not null;
            LayerOpacitySlider.Value = activeLayer?.Opacity ?? 100;
            LayerOpacityText.Text = activeLayer is null ? "-" : $"{activeLayer.Opacity:F0}%";
            LayerNameBox.IsEnabled = activeLayer is not null;
            LayerNameBox.Text = activeLayer?.Name ?? "";
            LayerBlendModeBox.IsEnabled = activeLayer is not null;
            SetComboByTag(LayerBlendModeBox, activeLayer?.BlendMode.ToString() ?? nameof(ImageBlendMode.Normal));
        }
        finally
        {
            _suppressLayerControlUpdate = false;
        }
    }

    private void ClearCurrentImage()
    {
        _currentBitmap = null;
        _originalBitmap = null;
        _restoreSourceBitmap = null;
        _compareBaselineBitmap = null;
        _currentPath = null;
        _compareMode = false;
        ClearLayers();
        ClearHistory();
        ResetInteractionModes();
        UpdatePreview(null, null);
    }

    private void PushUndo()
    {
        if (_currentBitmap is null)
        {
            return;
        }

        _undoStack.Push(new ImageState(_currentBitmap, _restoreSourceBitmap, CaptureLayers(), LayerList.SelectedIndex));
        TrimHistory(_undoStack, 30);
        _redoStack.Clear();
    }

    private void RestoreHistory(Stack<ImageState> from, Stack<ImageState> to, string status)
    {
        if (_currentBitmap is null || from.Count == 0)
        {
            SetStatus($"{status}할 기록이 없습니다.");
            return;
        }

        to.Push(new ImageState(_currentBitmap, _restoreSourceBitmap, CaptureLayers(), LayerList.SelectedIndex));
        ImageState state = from.Pop();
        _restoreSourceBitmap = state.RestoreSource;
        RestoreLayers(state.Layers, state.ActiveLayerIndex);
        if (state.Layers.Count > 0)
        {
            RefreshCompositeFromLayers();
        }
        else
        {
            _currentBitmap = state.Current;
            UpdatePreview(_currentBitmap, _currentPath);
        }

        RecordHistory(status);
        SetStatus(status);
    }

    private void ClearHistory()
    {
        _undoStack.Clear();
        _redoStack.Clear();
        _historyEntries.Clear();
        _historyIndex = -1;
        _historySequence = 0;
        _brushHistoryCaptured = false;
        UpdateHistorySummary();
    }

    private void HistoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressHistorySelection || HistoryList.SelectedItem is not HistoryEntry entry)
        {
            return;
        }

        if (_currentBitmap is not null)
        {
            PushUndo();
        }

        _historyIndex = HistoryList.SelectedIndex;
        _restoreSourceBitmap = entry.RestoreSource;
        RestoreLayers(entry.Layers, entry.ActiveLayerIndex);
        ResetInteractionModes();
        if (entry.Layers.Count > 0)
        {
            RefreshCompositeFromLayers();
        }
        else
        {
            _currentBitmap = entry.Bitmap;
            UpdatePreview(_currentBitmap, _currentPath);
        }

        UpdateHistorySummary();
        SetStatus($"기록 이동: {entry.Label}");
    }

    private void RecordHistory(string label)
    {
        if (_currentBitmap is null)
        {
            UpdateHistorySummary();
            return;
        }

        while (_historyEntries.Count > _historyIndex + 1)
        {
            _historyEntries.RemoveAt(_historyEntries.Count - 1);
        }

        _historySequence++;
        _historyEntries.Add(new HistoryEntry(
            _historySequence,
            label,
            BuildHistoryDetail(_currentBitmap),
            _currentBitmap,
            _restoreSourceBitmap,
            CaptureLayers(),
            LayerList.SelectedIndex));

        while (_historyEntries.Count > 50)
        {
            _historyEntries.RemoveAt(0);
        }

        _historyIndex = _historyEntries.Count - 1;
        SelectHistoryIndex(_historyIndex);
        UpdateHistorySummary();
    }

    private void SelectHistoryIndex(int index)
    {
        if (index < 0 || index >= _historyEntries.Count)
        {
            return;
        }

        _suppressHistorySelection = true;
        try
        {
            HistoryList.SelectedIndex = index;
            HistoryList.ScrollIntoView(_historyEntries[index]);
        }
        finally
        {
            _suppressHistorySelection = false;
        }
    }

    private void UpdateHistorySummary()
    {
        if (HistorySummaryText is null)
        {
            return;
        }

        HistorySummaryText.Text = _historyEntries.Count == 0
            ? "열린 이미지 없음"
            : $"{_historyEntries.Count}개 기록 · 현재 {_historyIndex + 1}";
    }

    private static string BuildHistoryDetail(BitmapSource bitmap)
    {
        return $"{bitmap.PixelWidth} x {bitmap.PixelHeight} · {bitmap.Format}";
    }

    private string GetBrushHistoryLabel()
    {
        if (_maskHideMode)
        {
            return "마스크 숨김 브러시";
        }

        if (_maskRevealMode)
        {
            return "마스크 복원 브러시";
        }

        if (_autoRestoreMode)
        {
            return "자동 복원 브러시";
        }

        return _restoreMode ? "복원 브러시" : "알파 지우개";
    }

    private static void TrimHistory<T>(Stack<T> stack, int maxCount)
    {
        if (stack.Count <= maxCount)
        {
            return;
        }

        T[] newestFirst = stack.Take(maxCount).ToArray();
        stack.Clear();
        for (int i = newestFirst.Length - 1; i >= 0; i--)
        {
            stack.Push(newestFirst[i]);
        }
    }

    private bool EnsureBitmap()
    {
        if (_currentBitmap is not null)
        {
            return true;
        }

        SetStatus("먼저 이미지를 선택하세요.");
        return false;
    }

    private void TransformCurrent(Func<BitmapSource, BitmapSource> transform, string status)
    {
        if (!EnsureBitmap())
        {
            return;
        }

        PushUndo();
        SetEditableBitmap(transform(GetEditableBitmap()!));
        if (_restoreSourceBitmap is not null)
        {
            _restoreSourceBitmap = transform(_restoreSourceBitmap);
        }

        RecordHistory(status);
        SetStatus(status);
    }

    private void ApplyBrush(Point imagePoint)
    {
        if (_currentBitmap is null)
        {
            return;
        }

        if (_autoRestoreMode && !CanUseAutoRestoreBrush())
        {
            _autoRestoreMode = false;
            _brushHistoryCaptured = false;
            UpdateBrushState();
            SetStatus("자동 복원 기준 이미지가 현재 편집 상태와 맞지 않습니다.");
            return;
        }

        if (!_brushHistoryCaptured)
        {
            PushUndo();
            _brushHistoryCaptured = true;
        }

        bool maskBrush = _maskHideMode || _maskRevealMode;
        int radius = (int)(maskBrush ? MaskBrushSizeSlider.Value : BrushSizeSlider.Value);
        double spacing = Math.Max(2.0, radius * (_autoRestoreMode ? 0.55 : 0.35));

        foreach (Point point in GetBrushStrokePoints(_lastBrushPoint, imagePoint, spacing))
        {
            ApplyBrushPoint(point, radius);
        }

        _lastBrushPoint = imagePoint;
        if (_layers.Count > 0)
        {
            RefreshCompositeFromLayers();
        }
        else
        {
            UpdatePreview(_currentBitmap, _currentPath);
        }

        UpdateBrushGhost(imagePoint);
    }

    private void ApplyBrushPoint(Point point, int radius)
    {
        int x = (int)Math.Round(point.X);
        int y = (int)Math.Round(point.Y);

        if (_maskHideMode || _maskRevealMode)
        {
            if (GetActiveLayer() is not ImageLayer activeLayer)
            {
                return;
            }

            BitmapSource mask = activeLayer.Mask
                ?? BitmapEditor.CreateMask(activeLayer.Bitmap.PixelWidth, activeLayer.Bitmap.PixelHeight, 255, activeLayer.Bitmap.DpiX, activeLayer.Bitmap.DpiY);
            BitmapSource editedMask = BitmapEditor.ApplyMaskBrush(
                mask,
                x,
                y,
                radius,
                reveal: _maskRevealMode,
                MaskBrushStrengthSlider.Value / 100.0);
            SetLayerMask(activeLayer, editedMask);
            return;
        }

        BitmapSource? target = GetEditableBitmap();
        if (target is null)
        {
            return;
        }

        BitmapSource edited = _autoRestoreMode
            ? BitmapEditor.ApplyAutoRestoreBrush(
                target,
                _restoreSourceBitmap!,
                x,
                y,
                radius,
                (int)AutoRestoreSensitivitySlider.Value,
                AutoRestoreStrengthSlider.Value / 100.0)
            : BitmapEditor.ApplyAlphaBrush(target, x, y, radius, restore: _restoreMode);

        SetEditableBitmap(edited, refreshPreview: false);
    }

    private void UpdateBrushGhost(Point imagePoint)
    {
        if (!IsBrushModeActive())
        {
            BrushGhost.Visibility = Visibility.Collapsed;
            return;
        }

        bool restorativeBrush = _restoreMode || _autoRestoreMode || _maskRevealMode;
        bool maskBrush = _maskHideMode || _maskRevealMode;
        double size = (maskBrush ? MaskBrushSizeSlider.Value : BrushSizeSlider.Value) * 2;
        BrushGhost.Width = size;
        BrushGhost.Height = size;
        BrushGhost.Stroke = restorativeBrush ? FindBrush("AccentBrush") : FindBrush("AccentWarmBrush");
        BrushGhost.Fill = new SolidColorBrush(restorativeBrush ? Color.FromArgb(34, 94, 234, 212) : Color.FromArgb(34, 255, 184, 107));
        Canvas.SetLeft(BrushGhost, imagePoint.X - (size / 2));
        Canvas.SetTop(BrushGhost, imagePoint.Y - (size / 2));
        BrushGhost.Visibility = Visibility.Visible;
    }

    private static IEnumerable<Point> GetBrushStrokePoints(Point? previous, Point current, double spacing)
    {
        if (previous is null)
        {
            yield return current;
            yield break;
        }

        double dx = current.X - previous.Value.X;
        double dy = current.Y - previous.Value.Y;
        double distance = Math.Sqrt((dx * dx) + (dy * dy));
        int steps = Math.Max(1, (int)Math.Ceiling(distance / spacing));

        for (int i = 1; i <= steps; i++)
        {
            double amount = i / (double)steps;
            yield return new Point(previous.Value.X + (dx * amount), previous.Value.Y + (dy * amount));
        }
    }

    private bool CanUseAutoRestoreBrush()
    {
        BitmapSource? target = GetEditableBitmap();
        return target is not null
            && _restoreSourceBitmap is not null
            && target.PixelWidth == _restoreSourceBitmap.PixelWidth
            && target.PixelHeight == _restoreSourceBitmap.PixelHeight;
    }

    private bool IsBrushModeActive()
    {
        return _eraseMode || _restoreMode || _autoRestoreMode || _maskHideMode || _maskRevealMode;
    }

    private bool TryGetImagePoint(Point hostPoint, out Point imagePoint)
    {
        imagePoint = default;
        if (_currentBitmap is null)
        {
            return false;
        }

        double x = Math.Clamp(hostPoint.X, 0, _currentBitmap.PixelWidth - 1);
        double y = Math.Clamp(hostPoint.Y, 0, _currentBitmap.PixelHeight - 1);
        imagePoint = new Point(x, y);
        return true;
    }

    private static Color SampleColor(BitmapSource source, int x, int y)
    {
        source = BitmapEditor.ToBgra32(source);
        x = Math.Clamp(x, 0, source.PixelWidth - 1);
        y = Math.Clamp(y, 0, source.PixelHeight - 1);
        byte[] pixel = new byte[4];
        source.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return Color.FromRgb(pixel[2], pixel[1], pixel[0]);
    }

    private void UpdateBrushState()
    {
        UpdateBrushButtons();
        SetStatus(_eraseMode ? "지우개 브러시 활성화"
            : _restoreMode ? "복원 브러시 활성화"
            : _autoRestoreMode ? "자동 복원 브러시 활성화: 전경으로 판단되는 부분만 복구"
            : _maskHideMode ? "레이어 마스크 숨김 브러시 활성화"
            : _maskRevealMode ? "레이어 마스크 복원 브러시 활성화"
            : "브러시 해제");
    }

    private void UpdateBrushButtons()
    {
        EraserButton.Background = _eraseMode ? FindBrush("AccentWarmBrush") : FindBrush("PanelLiftBrush");
        RestoreButton.Background = _restoreMode ? FindBrush("AccentBrush") : FindBrush("PanelLiftBrush");
        AutoRestoreButton.Background = _autoRestoreMode ? FindBrush("AccentBrush") : FindBrush("PanelLiftBrush");
        MaskHideButton.Background = _maskHideMode ? FindBrush("AccentWarmBrush") : FindBrush("PanelLiftBrush");
        MaskRevealButton.Background = _maskRevealMode ? FindBrush("AccentBrush") : FindBrush("PanelLiftBrush");
    }

    private void ResetInteractionModes()
    {
        _cropMode = false;
        _isCropping = false;
        _pickChroma = false;
        _eraseMode = false;
        _restoreMode = false;
        _autoRestoreMode = false;
        _maskHideMode = false;
        _maskRevealMode = false;
        _lastBrushPoint = null;
        CropRect.Visibility = Visibility.Collapsed;
        BrushGhost.Visibility = Visibility.Collapsed;
        CropButton.Background = FindBrush("PanelLiftBrush");
        UpdateBrushButtons();
    }

    private void UpdateQueueText()
    {
        QueueCountText.Text = $"{_jobs.Count}개 항목";
    }

    private void SetStatus(string text)
    {
        StatusText.Text = text;
    }

    private Brush FindBrush(string resourceKey)
    {
        return (Brush)FindResource(resourceKey);
    }

    private static string GetComboTag(ComboBox comboBox, string fallback)
    {
        return comboBox.SelectedItem is ComboBoxItem item && item.Tag is not null
            ? item.Tag.ToString() ?? fallback
            : fallback;
    }

    private static int GetComboInt(ComboBox comboBox, int fallback)
    {
        return int.TryParse(GetComboTag(comboBox, fallback.ToString()), out int value)
            ? value
            : fallback;
    }

    private static void SetComboByTag(ComboBox comboBox, string tag)
    {
        for (int i = 0; i < comboBox.Items.Count; i++)
        {
            if (comboBox.Items[i] is ComboBoxItem item &&
                string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedIndex = i;
                return;
            }
        }
    }

    private static ImageBlendMode ParseBlendMode(string value)
    {
        return Enum.TryParse(value, ignoreCase: true, out ImageBlendMode mode)
            ? mode
            : ImageBlendMode.Normal;
    }

    private static int GetPositiveInt(string text, int fallback, int min, int max)
    {
        return int.TryParse(text, out int value)
            ? Math.Clamp(value, min, max)
            : fallback;
    }

    private IEnumerable<string> ExpandPaths(IEnumerable<string> paths)
    {
        foreach (string path in paths)
        {
            if (Directory.Exists(path))
            {
                foreach (string file in Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories))
                {
                    yield return file;
                }
            }
            else if (File.Exists(path))
            {
                yield return path;
            }
        }
    }

    private sealed record ImageState(
        BitmapSource Current,
        BitmapSource? RestoreSource,
        IReadOnlyList<ImageLayerSnapshot> Layers,
        int ActiveLayerIndex);
}
