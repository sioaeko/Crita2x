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
    private bool _selectionMode;
    private bool _lassoMode;
    private bool _isLassoSelecting;
    private bool _magicWandMode;
    private bool _isSelecting;
    private bool _moveLayerMode;
    private bool _isMovingLayer;
    private bool _isResizingLayer;
    private bool _layerMoveCaptured;
    private bool _layerResizeCaptured;
    private bool _pickChroma;
    private bool _eraseMode;
    private bool _restoreMode;
    private bool _autoRestoreMode;
    private bool _cloneStampMode;
    private bool _dodgeMode;
    private bool _burnMode;
    private bool _maskHideMode;
    private bool _maskRevealMode;
    private bool _brushHistoryCaptured;
    private bool _isPanning;
    private Point _panStart;
    private double _panStartHorizontalOffset;
    private double _panStartVerticalOffset;
    private double _zoom = 1.0;
    private Point _cropStart;
    private Point _selectionStart;
    private Point _layerMoveStart;
    private int _layerMoveStartOffsetX;
    private int _layerMoveStartOffsetY;
    private LayerResizeHandle _layerResizeHandle = LayerResizeHandle.None;
    private Point _layerResizeAnchor;
    private int _layerResizeStartOffsetX;
    private int _layerResizeStartOffsetY;
    private BitmapSource? _layerResizeStartBitmap;
    private BitmapSource? _layerResizeStartMask;
    private Int32Rect? _selectionRect;
    private BitmapSource? _selectionMask;
    private readonly List<Point> _lassoPoints = [];
    private Point? _lastBrushPoint;
    private Point? _cloneSourcePoint;
    private Point? _cloneStrokeStart;
    private Point? _cloneStrokeSourceStart;
    private BitmapSource? _cloneStrokeSourceBitmap;
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
    private bool _layerLockEditCaptured;
    private bool _suppressLevelsUpdate;
    private bool _suppressHslUpdate;

    public MainWindow()
    {
        InitializeComponent();
        JobList.ItemsSource = _jobs;
        HistoryList.ItemsSource = _historyEntries;
        LayerList.ItemsSource = _layers;
        ResizeSlider.ValueChanged += (_, _) => ResizeBox.Text = ((int)ResizeSlider.Value).ToString();
        UpdateHslSummary();
        UpdateLevelsSummary();
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

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            ResetInteractionModes();
            SetStatus("도구 선택을 해제했습니다.");
            e.Handled = true;
            return;
        }

        if (!IsTextInputFocused() && e.Key == Key.W)
        {
            MagicWandMode_Click(sender, e);
            e.Handled = true;
            return;
        }

        if (!IsTextInputFocused() && e.Key == Key.L)
        {
            LassoMode_Click(sender, e);
            e.Handled = true;
            return;
        }

        if (!_moveLayerMode || IsTextInputFocused())
        {
            return;
        }

        int step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
        bool handled = e.Key switch
        {
            Key.Left => NudgeActiveLayer(-step, 0, recordUndo: true),
            Key.Right => NudgeActiveLayer(step, 0, recordUndo: true),
            Key.Up => NudgeActiveLayer(0, -step, recordUndo: true),
            Key.Down => NudgeActiveLayer(0, step, recordUndo: true),
            _ => false
        };

        if (handled)
        {
            e.Handled = true;
        }
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
        UpdateMoveLayerButton();
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
        if (!SetEditableBitmap(edited))
        {
            return;
        }
        RecordHistory("가장자리 누끼");
        SetStatus("가장자리 배경을 투명 처리했습니다.");
    }

    private void PickColor_Click(object sender, RoutedEventArgs e)
    {
        _pickChroma = true;
        _eraseMode = false;
        _restoreMode = false;
        _autoRestoreMode = false;
        _cloneStampMode = false;
        _dodgeMode = false;
        _burnMode = false;
        _maskHideMode = false;
        _maskRevealMode = false;
        _selectionMode = false;
        ClearLassoMode();
        _magicWandMode = false;
        _isSelecting = false;
        _moveLayerMode = false;
        _isMovingLayer = false;
        ClearCloneStroke();
        UpdateBrushButtons();
        UpdateSelectionButton();
        UpdateMoveLayerButton();
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
        if (!SetEditableBitmap(edited))
        {
            return;
        }
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
        if (!SetEditableBitmap(BackgroundRemovalService.FeatherAlpha(GetEditableBitmap()!, radius: 2)))
        {
            return;
        }
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
        if (!SetEditableBitmap(BackgroundRemovalService.Defringe(GetEditableBitmap()!, amount: 55)))
        {
            return;
        }
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
        TransformAllLayers(
            bitmap => bitmap,
            layer => (layer.OffsetX - trimRect.X, layer.OffsetY - trimRect.Y),
            canvas => BitmapEditor.Crop(canvas, trimRect));
        if (_restoreSourceBitmap is not null)
        {
            _restoreSourceBitmap = BitmapEditor.Crop(_restoreSourceBitmap, trimRect);
        }

        ClearSelectionOverlay(disableMode: true);
        RecordHistory("투명 여백 자르기");
        SetStatus("투명 여백을 잘랐습니다.");
    }

    private void Eraser_Click(object sender, RoutedEventArgs e)
    {
        _eraseMode = !_eraseMode;
        _restoreMode = false;
        _autoRestoreMode = false;
        _cloneStampMode = false;
        _dodgeMode = false;
        _burnMode = false;
        _maskHideMode = false;
        _maskRevealMode = false;
        _selectionMode = false;
        ClearLassoMode();
        _magicWandMode = false;
        _isSelecting = false;
        _moveLayerMode = false;
        _isMovingLayer = false;
        _pickChroma = false;
        ClearCloneStroke();
        UpdateBrushState();
    }

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        _restoreMode = !_restoreMode;
        _eraseMode = false;
        _autoRestoreMode = false;
        _cloneStampMode = false;
        _dodgeMode = false;
        _burnMode = false;
        _maskHideMode = false;
        _maskRevealMode = false;
        _selectionMode = false;
        ClearLassoMode();
        _magicWandMode = false;
        _isSelecting = false;
        _moveLayerMode = false;
        _isMovingLayer = false;
        _pickChroma = false;
        ClearCloneStroke();
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
        _cloneStampMode = false;
        _dodgeMode = false;
        _burnMode = false;
        _maskHideMode = false;
        _maskRevealMode = false;
        _selectionMode = false;
        ClearLassoMode();
        _magicWandMode = false;
        _isSelecting = false;
        _moveLayerMode = false;
        _isMovingLayer = false;
        _pickChroma = false;
        ClearCloneStroke();

        if (_autoRestoreMode && !CanUseAutoRestoreBrush())
        {
            _autoRestoreMode = false;
            UpdateBrushState();
            SetStatus("자동 복원은 원본과 현재 이미지 크기가 같을 때만 사용할 수 있습니다.");
            return;
        }

        UpdateBrushState();
    }

    private void CloneStamp_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureBitmap())
        {
            return;
        }

        _cloneStampMode = !_cloneStampMode;
        _eraseMode = false;
        _restoreMode = false;
        _autoRestoreMode = false;
        _dodgeMode = false;
        _burnMode = false;
        _maskHideMode = false;
        _maskRevealMode = false;
        _selectionMode = false;
        ClearLassoMode();
        _magicWandMode = false;
        _isSelecting = false;
        _moveLayerMode = false;
        _isMovingLayer = false;
        _pickChroma = false;
        ClearCloneStroke();
        UpdateBrushState();

        if (_cloneStampMode && _cloneSourcePoint is null)
        {
            SetStatus("복제 도장 활성화: Alt+클릭으로 원본 위치를 먼저 찍으세요.");
        }
    }

    private void Dodge_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureBitmap())
        {
            return;
        }

        _dodgeMode = !_dodgeMode;
        _burnMode = false;
        _eraseMode = false;
        _restoreMode = false;
        _autoRestoreMode = false;
        _cloneStampMode = false;
        _maskHideMode = false;
        _maskRevealMode = false;
        _selectionMode = false;
        ClearLassoMode();
        _magicWandMode = false;
        _isSelecting = false;
        _moveLayerMode = false;
        _isMovingLayer = false;
        _pickChroma = false;
        ClearCloneStroke();
        UpdateBrushState();
    }

    private void Burn_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureBitmap())
        {
            return;
        }

        _burnMode = !_burnMode;
        _dodgeMode = false;
        _eraseMode = false;
        _restoreMode = false;
        _autoRestoreMode = false;
        _cloneStampMode = false;
        _maskHideMode = false;
        _maskRevealMode = false;
        _selectionMode = false;
        ClearLassoMode();
        _magicWandMode = false;
        _isSelecting = false;
        _moveLayerMode = false;
        _isMovingLayer = false;
        _pickChroma = false;
        ClearCloneStroke();
        UpdateBrushState();
    }

    private void RotateLeft_Click(object sender, RoutedEventArgs e)
    {
        TransformCurrent(bitmap => BitmapEditor.Rotate(bitmap, -90), "왼쪽으로 회전했습니다.");
        ClearSelectionOverlay(disableMode: true);
    }

    private void RotateRight_Click(object sender, RoutedEventArgs e)
    {
        TransformCurrent(bitmap => BitmapEditor.Rotate(bitmap, 90), "오른쪽으로 회전했습니다.");
        ClearSelectionOverlay(disableMode: true);
    }

    private void FlipHorizontal_Click(object sender, RoutedEventArgs e)
    {
        TransformCurrent(BitmapEditor.FlipHorizontal, "좌우 반전했습니다.");
        ClearSelectionOverlay(disableMode: true);
    }

    private void FlipVertical_Click(object sender, RoutedEventArgs e)
    {
        TransformCurrent(BitmapEditor.FlipVertical, "상하 반전했습니다.");
        ClearSelectionOverlay(disableMode: true);
    }

    private void CropMode_Click(object sender, RoutedEventArgs e)
    {
        _cropMode = !_cropMode;
        _eraseMode = false;
        _restoreMode = false;
        _autoRestoreMode = false;
        _cloneStampMode = false;
        _dodgeMode = false;
        _burnMode = false;
        _maskHideMode = false;
        _maskRevealMode = false;
        _selectionMode = false;
        ClearLassoMode();
        _magicWandMode = false;
        _isSelecting = false;
        _moveLayerMode = false;
        _isMovingLayer = false;
        _pickChroma = false;
        ClearCloneStroke();
        CropButton.Background = _cropMode ? FindBrush("AccentBrush") : FindBrush("PanelLiftBrush");
        UpdateBrushButtons();
        UpdateSelectionButton();
        UpdateMoveLayerButton();
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

        TransformAllLayers(
            bitmap => bitmap,
            layer => (layer.OffsetX - rect.X, layer.OffsetY - rect.Y),
            canvas => BitmapEditor.Crop(canvas, rect));
        if (_restoreSourceBitmap is not null)
        {
            _restoreSourceBitmap = BitmapEditor.Crop(_restoreSourceBitmap, rect);
        }

        _cropMode = false;
        CropRect.Visibility = Visibility.Collapsed;
        ClearSelectionOverlay(disableMode: true);
        RecordHistory("자르기");
        SetStatus("선택 영역으로 잘랐습니다.");
    }

    private void SelectionMode_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureBitmap())
        {
            return;
        }

        _selectionMode = !_selectionMode;
        ClearLassoMode();
        _magicWandMode = false;
        _cropMode = false;
        _isCropping = false;
        _eraseMode = false;
        _restoreMode = false;
        _autoRestoreMode = false;
        _cloneStampMode = false;
        _dodgeMode = false;
        _burnMode = false;
        _maskHideMode = false;
        _maskRevealMode = false;
        _moveLayerMode = false;
        _isMovingLayer = false;
        _pickChroma = false;
        ClearCloneStroke();
        CropRect.Visibility = Visibility.Collapsed;
        UpdateBrushButtons();
        UpdateSelectionButton();
        UpdateMoveLayerButton();
        SetStatus(_selectionMode ? "캔버스에서 선택할 영역을 드래그하세요." : "선택 영역 도구 해제");
    }

    private void LassoMode_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureBitmap())
        {
            return;
        }

        _lassoMode = !_lassoMode;
        _selectionMode = false;
        _magicWandMode = false;
        _isSelecting = false;
        _isLassoSelecting = false;
        _cropMode = false;
        _isCropping = false;
        _eraseMode = false;
        _restoreMode = false;
        _autoRestoreMode = false;
        _cloneStampMode = false;
        _dodgeMode = false;
        _burnMode = false;
        _maskHideMode = false;
        _maskRevealMode = false;
        _moveLayerMode = false;
        _isMovingLayer = false;
        _pickChroma = false;
        ClearCloneStroke();
        ClearLassoPath();
        CropRect.Visibility = Visibility.Collapsed;
        UpdateBrushButtons();
        UpdateSelectionButton();
        UpdateMoveLayerButton();
        SetStatus(_lassoMode ? "캔버스에서 자유롭게 드래그해 선택 영역을 그리세요." : "올가미 선택 해제");
    }

    private void MagicWandMode_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureBitmap())
        {
            return;
        }

        _magicWandMode = !_magicWandMode;
        ClearLassoMode();
        _selectionMode = false;
        _isSelecting = false;
        _isLassoSelecting = false;
        _cropMode = false;
        _isCropping = false;
        _eraseMode = false;
        _restoreMode = false;
        _autoRestoreMode = false;
        _cloneStampMode = false;
        _dodgeMode = false;
        _burnMode = false;
        _maskHideMode = false;
        _maskRevealMode = false;
        _moveLayerMode = false;
        _isMovingLayer = false;
        _pickChroma = false;
        ClearCloneStroke();
        CropRect.Visibility = Visibility.Collapsed;
        UpdateBrushButtons();
        UpdateSelectionButton();
        UpdateMoveLayerButton();
        SetStatus(_magicWandMode ? "캔버스에서 비슷한 색 영역을 클릭하세요." : "마술봉 선택 해제");
    }

    private void ClearSelection_Click(object sender, RoutedEventArgs e)
    {
        ClearSelectionOverlay(disableMode: true);
        SetStatus("선택 영역을 해제했습니다.");
    }

    private void ClearSelectionArea_Click(object sender, RoutedEventArgs e)
    {
        ApplySelectionAlpha(keepInside: false, "선택 영역 지우기", "선택 영역을 투명하게 지웠습니다.");
    }

    private void KeepSelectionArea_Click(object sender, RoutedEventArgs e)
    {
        ApplySelectionAlpha(keepInside: true, "선택만 남기기", "선택 영역만 남기고 나머지를 투명하게 지웠습니다.");
    }

    private void ExtractSelectionLayer_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetSelection(out Int32Rect rect) || GetActiveLayer() is not ImageLayer activeLayer)
        {
            SetStatus("새 레이어로 만들 선택 영역이 없습니다.");
            return;
        }

        int insertIndex = Math.Clamp(LayerList.SelectedIndex + 1, 0, _layers.Count);
        if (!TryGetSelectionMaskForTarget(activeLayer.Bitmap, activeLayer, out BitmapSource selectionMask))
        {
            SetStatus("선택 영역이 선택 레이어와 겹치지 않습니다.");
            return;
        }

        PushUndo();
        BitmapSource extracted = BitmapEditor.ApplySelectionMaskAlpha(activeLayer.Bitmap, selectionMask, keepInside: true);
        var layer = new ImageLayer("선택 레이어", extracted)
        {
            OffsetX = activeLayer.OffsetX,
            OffsetY = activeLayer.OffsetY
        };
        AddLayer(layer, insertIndex);
        SelectLayer(layer);
        RefreshCompositeFromLayers();
        RecordHistory("선택 영역 레이어 추출");
        SetStatus("선택 영역을 새 레이어로 만들었습니다.");
    }

    private void SelectionToMask_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetSelection(out Int32Rect rect) || GetActiveLayer() is not ImageLayer activeLayer)
        {
            SetStatus("마스크로 만들 선택 영역이 없습니다.");
            return;
        }

        if (!EnsureLayerEditable(activeLayer, "마스크 편집"))
        {
            return;
        }

        if (!TryGetSelectionMaskForTarget(activeLayer.Bitmap, activeLayer, out BitmapSource mask))
        {
            SetStatus("선택 영역이 선택 레이어와 겹치지 않습니다.");
            return;
        }

        PushUndo();
        SetLayerMask(activeLayer, mask);
        RefreshCompositeFromLayers();
        RecordHistory("선택 영역 마스크화");
        SetStatus("선택 영역을 레이어 마스크로 바꿨습니다.");
    }

    private void ApplySelectionAlpha(bool keepInside, string historyLabel, string status)
    {
        if (!TryGetSelection(out Int32Rect rect))
        {
            SetStatus("적용할 선택 영역이 없습니다.");
            return;
        }

        BitmapSource? target = GetEditableBitmap();
        if (target is null)
        {
            SetStatus("편집할 이미지가 없습니다.");
            return;
        }

        ImageLayer? activeLayer = GetActiveLayer();
        if (!TryGetSelectionMaskForTarget(target, activeLayer, out BitmapSource selectionMask))
        {
            SetStatus("선택 영역이 편집 대상과 겹치지 않습니다.");
            return;
        }

        PushUndo();
        BitmapSource edited = BitmapEditor.ApplySelectionMaskAlpha(target, selectionMask, keepInside);
        if (!SetEditableBitmap(edited))
        {
            return;
        }
        RecordHistory(historyLabel);
        SetStatus(status);
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
        TransformAllLayers(
            bitmap => BitmapEditor.Resize(bitmap, width, height),
            layer => ((int)Math.Round(layer.OffsetX * ratio), (int)Math.Round(layer.OffsetY * ratio)));
        if (_restoreSourceBitmap is not null)
        {
            _restoreSourceBitmap = BitmapEditor.Resize(_restoreSourceBitmap, width, height);
        }

        ClearSelectionOverlay(disableMode: true);
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
        if (!SetEditableBitmap(BitmapEditor.ApplyAdjustments(GetEditableBitmap()!, settings)))
        {
            return;
        }
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

    private void ApplyHsl_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureBitmap())
        {
            return;
        }

        HslSettings settings = GetHslSettings();
        if (Math.Abs(settings.HueShift) < 0.01
            && Math.Abs(settings.Saturation) < 0.01
            && Math.Abs(settings.Lightness) < 0.01)
        {
            SetStatus("적용할 HSL 보정 값이 없습니다.");
            return;
        }

        PushUndo();
        if (!SetEditableBitmap(BitmapEditor.ApplyHueSaturation(GetEditableBitmap()!, settings)))
        {
            return;
        }

        if (_restoreSourceBitmap is not null)
        {
            _restoreSourceBitmap = BitmapEditor.ApplyHueSaturation(_restoreSourceBitmap, settings);
        }

        RecordHistory("HSL 보정");
        SetStatus($"HSL 보정 적용: 색상 {settings.HueShift:F0}°, 채도 {settings.Saturation:F0}, 명도 {settings.Lightness:F0}");
    }

    private void ResetHsl_Click(object sender, RoutedEventArgs e)
    {
        SetHslSettings(new HslSettings(0, 0, 0));
        SetStatus("HSL 값을 초기화했습니다.");
    }

    private void ApplyLevels_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureBitmap())
        {
            return;
        }

        LevelSettings settings = GetLevelSettings();
        PushUndo();
        if (!SetEditableBitmap(BitmapEditor.ApplyLevels(GetEditableBitmap()!, settings)))
        {
            return;
        }
        if (_restoreSourceBitmap is not null)
        {
            _restoreSourceBitmap = BitmapEditor.ApplyLevels(_restoreSourceBitmap, settings);
        }

        RecordHistory("레벨 보정");
        SetStatus("레벨 보정을 적용했습니다.");
    }

    private void AutoLevels_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureBitmap())
        {
            return;
        }

        SetLevelSliders(BitmapEditor.CreateAutoLevelSettings(GetEditableBitmap()!));
        SetStatus("자동 레벨 값을 계산했습니다. 적용 버튼으로 반영하세요.");
    }

    private void ResetLevels_Click(object sender, RoutedEventArgs e)
    {
        SetLevelSliders(new LevelSettings(0, 255, 1.0, 0, 255));
        SetStatus("레벨 값을 초기화했습니다.");
    }

    private void HslSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressHslUpdate)
        {
            return;
        }

        UpdateHslSummary();
    }

    private void LevelsSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressLevelsUpdate)
        {
            return;
        }

        NormalizeLevelSliders();
        UpdateLevelsSummary();
    }

    private void HistogramHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateHistogramPreview();
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
            OffsetX = activeLayer.OffsetX,
            OffsetY = activeLayer.OffsetY,
            IsVisible = activeLayer.IsVisible,
            IsLocked = false,
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

    private void BringLayerToTop_Click(object sender, RoutedEventArgs e)
    {
        MoveActiveLayerToIndex(_layers.Count - 1, "레이어 맨 위로 이동", "선택 레이어를 맨 위로 가져왔습니다.");
    }

    private void BringLayerForward_Click(object sender, RoutedEventArgs e)
    {
        if (GetActiveLayer() is not ImageLayer activeLayer)
        {
            SetStatus("순서를 바꿀 레이어가 없습니다.");
            return;
        }

        MoveActiveLayerToIndex(_layers.IndexOf(activeLayer) + 1, "레이어 앞으로 이동", "선택 레이어를 한 단계 앞으로 가져왔습니다.");
    }

    private void SendLayerBackward_Click(object sender, RoutedEventArgs e)
    {
        if (GetActiveLayer() is not ImageLayer activeLayer)
        {
            SetStatus("순서를 바꿀 레이어가 없습니다.");
            return;
        }

        MoveActiveLayerToIndex(_layers.IndexOf(activeLayer) - 1, "레이어 뒤로 이동", "선택 레이어를 한 단계 뒤로 보냈습니다.");
    }

    private void SendLayerToBottom_Click(object sender, RoutedEventArgs e)
    {
        MoveActiveLayerToIndex(0, "레이어 맨 아래로 이동", "선택 레이어를 맨 아래로 보냈습니다.");
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

        if (!EnsureLayerEditable(activeLayer, "마스크 편집"))
        {
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

        if (!EnsureLayerEditable(activeLayer, "마스크 편집"))
        {
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

        if (!EnsureLayerEditable(activeLayer, "마스크 편집"))
        {
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
        _cloneStampMode = false;
        _dodgeMode = false;
        _burnMode = false;
        _selectionMode = false;
        ClearLassoMode();
        _magicWandMode = false;
        _isSelecting = false;
        _moveLayerMode = false;
        _isMovingLayer = false;
        _pickChroma = false;
        ClearCloneStroke();
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
        _cloneStampMode = false;
        _dodgeMode = false;
        _burnMode = false;
        _selectionMode = false;
        ClearLassoMode();
        _magicWandMode = false;
        _isSelecting = false;
        _moveLayerMode = false;
        _isMovingLayer = false;
        _pickChroma = false;
        ClearCloneStroke();
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

    private void LayerLock_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressLayerRefresh)
        {
            return;
        }

        if (TryGetLayerFromSender(sender, out ImageLayer layer))
        {
            SelectLayer(layer);
        }
        else
        {
            if (GetActiveLayer() is not ImageLayer activeLayer)
            {
                return;
            }

            layer = activeLayer;
        }

        _layerLockEditCaptured = false;
        UpdateLayerControls();
        RecordHistory("레이어 잠금 변경");
        SetStatus(layer.IsLocked
            ? "선택 레이어를 잠갔습니다."
            : "선택 레이어 잠금을 해제했습니다.");
    }

    private void LayerLock_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_layerLockEditCaptured || !TryGetLayerFromSender(sender, out _))
        {
            return;
        }

        PushUndo();
        _layerLockEditCaptured = true;
    }

    private void LayerLockBox_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressLayerControlUpdate || GetActiveLayer() is not ImageLayer activeLayer)
        {
            return;
        }

        PushUndo();
        activeLayer.IsLocked = LayerLockBox.IsChecked == true;
        UpdateLayerControls();
        RecordHistory("레이어 잠금 변경");
        SetStatus(activeLayer.IsLocked ? "선택 레이어를 잠갔습니다." : "선택 레이어 잠금을 해제했습니다.");
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

    private void MoveLayerMode_Click(object sender, RoutedEventArgs e)
    {
        if (GetActiveLayer() is not ImageLayer activeLayer)
        {
            SetStatus("이동할 레이어가 없습니다.");
            return;
        }

        if (!EnsureLayerEditable(activeLayer, "이동"))
        {
            return;
        }

        _moveLayerMode = !_moveLayerMode;
        _cropMode = false;
        _isCropping = false;
        _selectionMode = false;
        ClearLassoMode();
        _magicWandMode = false;
        _isSelecting = false;
        _eraseMode = false;
        _restoreMode = false;
        _autoRestoreMode = false;
        _cloneStampMode = false;
        _dodgeMode = false;
        _burnMode = false;
        _maskHideMode = false;
        _maskRevealMode = false;
        _pickChroma = false;
        ClearCloneStroke();
        CropRect.Visibility = Visibility.Collapsed;
        UpdateBrushButtons();
        UpdateSelectionButton();
        UpdateMoveLayerButton();
        SetStatus(_moveLayerMode ? "캔버스에서 선택 레이어를 드래그하세요. Shift+방향키는 10px 이동입니다." : "레이어 이동 도구 해제");
    }

    private void ApplyLayerPosition_Click(object sender, RoutedEventArgs e)
    {
        if (GetActiveLayer() is not ImageLayer activeLayer)
        {
            SetStatus("위치를 적용할 레이어가 없습니다.");
            return;
        }

        if (!EnsureLayerEditable(activeLayer, "이동"))
        {
            return;
        }

        int x = int.TryParse(LayerOffsetXBox.Text, out int parsedX) ? parsedX : activeLayer.OffsetX;
        int y = int.TryParse(LayerOffsetYBox.Text, out int parsedY) ? parsedY : activeLayer.OffsetY;
        if (x == activeLayer.OffsetX && y == activeLayer.OffsetY)
        {
            return;
        }

        PushUndo();
        SetLayerOffset(activeLayer, x, y);
        RecordHistory("레이어 위치 변경");
        SetStatus($"레이어 위치: X {x}, Y {y}");
    }

    private void ResetLayerPosition_Click(object sender, RoutedEventArgs e)
    {
        if (GetActiveLayer() is not ImageLayer activeLayer)
        {
            SetStatus("위치를 초기화할 레이어가 없습니다.");
            return;
        }

        if (!EnsureLayerEditable(activeLayer, "이동"))
        {
            return;
        }

        if (activeLayer.OffsetX == 0 && activeLayer.OffsetY == 0)
        {
            return;
        }

        PushUndo();
        SetLayerOffset(activeLayer, 0, 0);
        RecordHistory("레이어 원점 이동");
        SetStatus("선택 레이어를 원점으로 이동했습니다.");
    }

    private void CenterLayer_Click(object sender, RoutedEventArgs e)
    {
        if (_currentBitmap is null || GetActiveLayer() is not ImageLayer activeLayer)
        {
            SetStatus("중앙에 배치할 레이어가 없습니다.");
            return;
        }

        if (!EnsureLayerEditable(activeLayer, "이동"))
        {
            return;
        }

        int x = (int)Math.Round((_currentBitmap.PixelWidth - activeLayer.Bitmap.PixelWidth) / 2.0);
        int y = (int)Math.Round((_currentBitmap.PixelHeight - activeLayer.Bitmap.PixelHeight) / 2.0);
        if (x == activeLayer.OffsetX && y == activeLayer.OffsetY)
        {
            return;
        }

        PushUndo();
        SetLayerOffset(activeLayer, x, y);
        RecordHistory("레이어 중앙 배치");
        SetStatus("선택 레이어를 캔버스 중앙에 배치했습니다.");
    }

    private void NudgeLayer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string direction })
        {
            return;
        }

        int step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
        _ = direction switch
        {
            "left" => NudgeActiveLayer(-step, 0, recordUndo: true),
            "right" => NudgeActiveLayer(step, 0, recordUndo: true),
            "up" => NudgeActiveLayer(0, -step, recordUndo: true),
            "down" => NudgeActiveLayer(0, step, recordUndo: true),
            _ => false
        };
    }

    private void AlignLayer_Click(object sender, RoutedEventArgs e)
    {
        if (_currentBitmap is null || GetActiveLayer() is not ImageLayer activeLayer)
        {
            SetStatus("정렬할 레이어가 없습니다.");
            return;
        }

        if (!EnsureLayerEditable(activeLayer, "정렬"))
        {
            return;
        }

        if (sender is not FrameworkElement { Tag: string alignment })
        {
            return;
        }

        int x = activeLayer.OffsetX;
        int y = activeLayer.OffsetY;
        x = alignment switch
        {
            "left" => 0,
            "center-x" => (int)Math.Round((_currentBitmap.PixelWidth - activeLayer.Bitmap.PixelWidth) / 2.0),
            "right" => _currentBitmap.PixelWidth - activeLayer.Bitmap.PixelWidth,
            _ => x
        };
        y = alignment switch
        {
            "top" => 0,
            "center-y" => (int)Math.Round((_currentBitmap.PixelHeight - activeLayer.Bitmap.PixelHeight) / 2.0),
            "bottom" => _currentBitmap.PixelHeight - activeLayer.Bitmap.PixelHeight,
            _ => y
        };

        if (x == activeLayer.OffsetX && y == activeLayer.OffsetY)
        {
            return;
        }

        PushUndo();
        SetLayerOffset(activeLayer, x, y);
        RecordHistory("레이어 정렬");
        SetStatus($"레이어 정렬: X {x}, Y {y}");
    }

    private void ApplyLayerSize_Click(object sender, RoutedEventArgs e)
    {
        if (GetActiveLayer() is not ImageLayer activeLayer)
        {
            SetStatus("크기를 바꿀 레이어가 없습니다.");
            return;
        }

        int currentWidth = activeLayer.Bitmap.PixelWidth;
        int currentHeight = activeLayer.Bitmap.PixelHeight;
        bool widthOk = int.TryParse(LayerWidthBox.Text, out int width) && width > 0;
        bool heightOk = int.TryParse(LayerHeightBox.Text, out int height) && height > 0;
        if (!widthOk && !heightOk)
        {
            SetStatus("레이어 너비나 높이를 입력하세요.");
            return;
        }

        width = widthOk ? width : currentWidth;
        height = heightOk ? height : currentHeight;
        if (LayerAspectLockBox.IsChecked == true)
        {
            double ratio = currentHeight / (double)Math.Max(1, currentWidth);
            if (widthOk)
            {
                height = Math.Max(1, (int)Math.Round(width * ratio));
            }
            else if (heightOk)
            {
                width = Math.Max(1, (int)Math.Round(height / ratio));
            }
        }

        width = Math.Clamp(width, 1, 32768);
        height = Math.Clamp(height, 1, 32768);
        if (width == currentWidth && height == currentHeight)
        {
            return;
        }

        ResizeActiveLayer(activeLayer, width, height, centerOnCanvas: false, "레이어 크기 변경", $"레이어 크기: {width} x {height}");
    }

    private void FitLayerToCanvas_Click(object sender, RoutedEventArgs e)
    {
        ResizeLayerToCanvas(fill: false);
    }

    private void FillLayerToCanvas_Click(object sender, RoutedEventArgs e)
    {
        ResizeLayerToCanvas(fill: true);
    }

    private void RotateLayerLeft_Click(object sender, RoutedEventArgs e)
    {
        TransformActiveLayer(bitmap => BitmapEditor.Rotate(bitmap, -90), "레이어 왼쪽 회전", "선택 레이어를 왼쪽으로 회전했습니다.", keepCenter: true);
    }

    private void RotateLayerRight_Click(object sender, RoutedEventArgs e)
    {
        TransformActiveLayer(bitmap => BitmapEditor.Rotate(bitmap, 90), "레이어 오른쪽 회전", "선택 레이어를 오른쪽으로 회전했습니다.", keepCenter: true);
    }

    private void FlipLayerHorizontal_Click(object sender, RoutedEventArgs e)
    {
        TransformActiveLayer(BitmapEditor.FlipHorizontal, "레이어 좌우 반전", "선택 레이어를 좌우 반전했습니다.", keepCenter: false);
    }

    private void FlipLayerVertical_Click(object sender, RoutedEventArgs e)
    {
        TransformActiveLayer(BitmapEditor.FlipVertical, "레이어 상하 반전", "선택 레이어를 상하 반전했습니다.", keepCenter: false);
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

    private static bool TryGetLayerFromSender(object sender, out ImageLayer layer)
    {
        if (sender is FrameworkElement { DataContext: ImageLayer imageLayer })
        {
            layer = imageLayer;
            return true;
        }

        layer = null!;
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
        ToolbarOpenLastOutputButton.IsEnabled = hasOutput;
        ToolbarRevealLastOutputButton.IsEnabled = hasOutput;
        PanelOpenLastOutputButton.IsEnabled = hasOutput;
        PanelRevealLastOutputButton.IsEnabled = hasOutput;
        CompletionTray.Visibility = hasOutput ? Visibility.Visible : Visibility.Collapsed;

        string outputName = hasOutput
            ? Path.GetFileName(_lastOutputPath!)
            : "완료 결과 없음";
        LastOutputNameText.Text = outputName;
        PanelLastOutputNameText.Text = outputName;
        PreviewLastOutputNameText.Text = outputName;
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

    private void GridOverlay_Changed(object sender, RoutedEventArgs e)
    {
        UpdateGridOverlay();
    }

    private void GridSizeBox_LostFocus(object sender, RoutedEventArgs e)
    {
        GridSizeBox.Text = GetGridSize().ToString();
        UpdateGridOverlay();
    }

    private void UpdateGridOverlay()
    {
        if (_currentBitmap is null || GridOverlayBox.IsChecked != true)
        {
            GridOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        int size = GetGridSize();
        GridOverlay.Width = _currentBitmap.PixelWidth;
        GridOverlay.Height = _currentBitmap.PixelHeight;
        GridOverlay.Fill = CreateGridBrush(size);
        GridOverlay.Visibility = Visibility.Visible;
    }

    private int GetGridSize()
    {
        return int.TryParse(GridSizeBox.Text, out int size)
            ? Math.Clamp(size, 4, 512)
            : 64;
    }

    private static DrawingBrush CreateGridBrush(int size)
    {
        var group = new DrawingGroup();
        using (DrawingContext context = group.Open())
        {
            var linePen = new Pen(new SolidColorBrush(Color.FromArgb(58, 244, 240, 232)), 1);
            var majorPen = new Pen(new SolidColorBrush(Color.FromArgb(86, 130, 220, 207)), 1);
            Rect tile = new(0, 0, size, size);
            context.DrawRectangle(Brushes.Transparent, null, tile);
            context.DrawLine(linePen, new Point(size - 0.5, 0), new Point(size - 0.5, size));
            context.DrawLine(linePen, new Point(0, size - 0.5), new Point(size, size - 0.5));
            context.DrawLine(majorPen, new Point(0.5, 0), new Point(0.5, size));
            context.DrawLine(majorPen, new Point(0, 0.5), new Point(size, 0.5));
        }

        return new DrawingBrush(group)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, size, size),
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None,
            AlignmentX = AlignmentX.Left,
            AlignmentY = AlignmentY.Top
        };
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

        if (_cloneStampMode && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            _cloneSourcePoint = imagePoint;
            ClearCloneStroke();
            UpdateCloneSourceMarker();
            SetStatus($"복제 원본 지정: X {(int)Math.Round(imagePoint.X)}, Y {(int)Math.Round(imagePoint.Y)}");
            return;
        }

        if (IsBrushModeActive())
        {
            Mouse.Capture(ImageHost);
            ApplyBrush(imagePoint);
            return;
        }

        if (_lassoMode)
        {
            BeginLassoSelection(imagePoint);
            return;
        }

        if (_magicWandMode)
        {
            ApplyMagicWandSelection(imagePoint);
            return;
        }

        if (!_selectionMode && !_cropMode && TryGetLayerResizeHandle(imagePoint, out LayerResizeHandle resizeHandle))
        {
            BeginLayerResize(resizeHandle, imagePoint);
            return;
        }

        if (_moveLayerMode)
        {
            if (GetActiveLayer() is not ImageLayer activeLayer)
            {
                SetStatus("이동할 레이어가 없습니다.");
                return;
            }

            _isMovingLayer = true;
            _layerMoveCaptured = false;
            _layerMoveStart = imagePoint;
            _layerMoveStartOffsetX = activeLayer.OffsetX;
            _layerMoveStartOffsetY = activeLayer.OffsetY;
            Mouse.Capture(ImageHost);
            ImageHost.Cursor = Cursors.SizeAll;
            return;
        }

        if (_selectionMode)
        {
            _isSelecting = true;
            _selectionStart = imagePoint;
            Mouse.Capture(ImageHost);
            SetSelectionFromPoints(_selectionStart, imagePoint);
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
            UpdateLayerHandleCursor(imagePoint);
        }

        if (_isResizingLayer && e.LeftButton == MouseButtonState.Pressed && TryGetImagePoint(point, out imagePoint))
        {
            ResizeActiveLayerTo(imagePoint);
        }
        else if (_isMovingLayer && e.LeftButton == MouseButtonState.Pressed && TryGetImagePoint(point, out imagePoint))
        {
            MoveActiveLayerTo(imagePoint);
        }
        else if (_isLassoSelecting && e.LeftButton == MouseButtonState.Pressed && TryGetImagePoint(point, out imagePoint))
        {
            AddLassoPoint(imagePoint);
        }
        else if (_isSelecting && e.LeftButton == MouseButtonState.Pressed && TryGetImagePoint(point, out imagePoint))
        {
            SetSelectionFromPoints(_selectionStart, imagePoint);
        }
        else if (_isCropping && e.LeftButton == MouseButtonState.Pressed && TryGetImagePoint(point, out imagePoint))
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
        if (_isResizingLayer)
        {
            _isResizingLayer = false;
            if (_layerResizeCaptured)
            {
                RecordHistory("레이어 자유 변형");
                if (GetActiveLayer() is ImageLayer activeLayer)
                {
                    SetStatus($"레이어 크기: {activeLayer.Bitmap.PixelWidth} x {activeLayer.Bitmap.PixelHeight}");
                }
            }
        }

        if (_isMovingLayer)
        {
            _isMovingLayer = false;
            if (_layerMoveCaptured)
            {
                RecordHistory("레이어 이동");
                if (GetActiveLayer() is ImageLayer activeLayer)
                {
                    SetStatus($"레이어 위치: X {activeLayer.OffsetX}, Y {activeLayer.OffsetY}");
                }
            }
        }

        if (_isLassoSelecting)
        {
            _isLassoSelecting = false;
            FinalizeLassoSelection();
        }

        if (_isSelecting)
        {
            _isSelecting = false;
            SetStatus(_selectionRect is { } rect
                ? $"선택 영역: {rect.Width} x {rect.Height}"
                : "선택 영역이 너무 작습니다.");
        }

        _isPanning = false;
        _layerMoveCaptured = false;
        ClearLayerResizeState();
        _brushHistoryCaptured = false;
        _lastBrushPoint = null;
        ClearCloneStroke();
        ImageHost.Cursor = null;
        Mouse.Capture(null);
    }

    private void Preview_MouseLeave(object sender, MouseEventArgs e)
    {
        BrushGhost.Visibility = Visibility.Collapsed;
    }

    private void SetSelectionFromPoints(Point start, Point end)
    {
        if (_currentBitmap is null)
        {
            return;
        }

        int x = (int)Math.Floor(Math.Min(start.X, end.X));
        int y = (int)Math.Floor(Math.Min(start.Y, end.Y));
        int right = (int)Math.Ceiling(Math.Max(start.X, end.X));
        int bottom = (int)Math.Ceiling(Math.Max(start.Y, end.Y));
        x = Math.Clamp(x, 0, Math.Max(0, _currentBitmap.PixelWidth - 1));
        y = Math.Clamp(y, 0, Math.Max(0, _currentBitmap.PixelHeight - 1));
        right = Math.Clamp(right, x + 1, _currentBitmap.PixelWidth);
        bottom = Math.Clamp(bottom, y + 1, _currentBitmap.PixelHeight);

        int width = right - x;
        int height = bottom - y;
        _selectionRect = width < 2 || height < 2
            ? null
            : new Int32Rect(x, y, width, height);
        _selectionMask = null;
        UpdateSelectionOverlay();
    }

    private void BeginLassoSelection(Point imagePoint)
    {
        _isLassoSelecting = true;
        _lassoPoints.Clear();
        _selectionMask = null;
        _selectionRect = null;
        SelectionMaskOverlay.Source = null;
        SelectionMaskOverlay.Visibility = Visibility.Collapsed;
        SelectionRect.Visibility = Visibility.Collapsed;
        Mouse.Capture(ImageHost);
        AddLassoPoint(imagePoint, force: true);
    }

    private void AddLassoPoint(Point imagePoint, bool force = false)
    {
        if (_currentBitmap is null)
        {
            return;
        }

        var clamped = new Point(
            Math.Clamp(imagePoint.X, 0, Math.Max(0, _currentBitmap.PixelWidth - 1)),
            Math.Clamp(imagePoint.Y, 0, Math.Max(0, _currentBitmap.PixelHeight - 1)));

        if (!force && _lassoPoints.Count > 0)
        {
            Point previous = _lassoPoints[^1];
            double dx = clamped.X - previous.X;
            double dy = clamped.Y - previous.Y;
            if ((dx * dx) + (dy * dy) < 4)
            {
                return;
            }
        }

        _lassoPoints.Add(clamped);
        UpdateLassoPath();
    }

    private void FinalizeLassoSelection()
    {
        if (_currentBitmap is null)
        {
            ClearLassoPath();
            return;
        }

        if (_lassoPoints.Count < 3)
        {
            ClearLassoPath();
            SetStatus("올가미 선택 영역이 너무 작습니다.");
            return;
        }

        double minX = _lassoPoints.Min(point => point.X);
        double minY = _lassoPoints.Min(point => point.Y);
        double maxX = _lassoPoints.Max(point => point.X);
        double maxY = _lassoPoints.Max(point => point.Y);
        int left = Math.Clamp((int)Math.Floor(minX), 0, Math.Max(0, _currentBitmap.PixelWidth - 1));
        int top = Math.Clamp((int)Math.Floor(minY), 0, Math.Max(0, _currentBitmap.PixelHeight - 1));
        int right = Math.Clamp((int)Math.Ceiling(maxX), left + 1, _currentBitmap.PixelWidth);
        int bottom = Math.Clamp((int)Math.Ceiling(maxY), top + 1, _currentBitmap.PixelHeight);
        if (right - left < 2 || bottom - top < 2)
        {
            ClearLassoPath();
            SetStatus("올가미 선택 영역이 너무 작습니다.");
            return;
        }

        _selectionMask = BitmapEditor.CreatePolygonSelectionMask(
            _currentBitmap.PixelWidth,
            _currentBitmap.PixelHeight,
            _lassoPoints,
            (int)SelectionFeatherSlider.Value,
            _currentBitmap.DpiX,
            _currentBitmap.DpiY);
        _selectionRect = new Int32Rect(left, top, right - left, bottom - top);
        ClearLassoPath();
        UpdateSelectionOverlay();
        UpdateSelectionButton();
        SetStatus($"올가미 선택: {right - left} x {bottom - top}");
    }

    private void UpdateLassoPath()
    {
        LassoPath.Points = new PointCollection(_lassoPoints);
        LassoPath.Visibility = _lassoPoints.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ClearLassoPath()
    {
        _lassoPoints.Clear();
        LassoPath.Points.Clear();
        LassoPath.Visibility = Visibility.Collapsed;
    }

    private void ClearLassoMode()
    {
        _lassoMode = false;
        _isLassoSelecting = false;
        ClearLassoPath();
    }

    private void ApplyMagicWandSelection(Point imagePoint)
    {
        if (_currentBitmap is null)
        {
            return;
        }

        BitmapSource source = _currentBitmap;
        int offsetX = 0;
        int offsetY = 0;
        if (GetActiveLayer() is ImageLayer activeLayer)
        {
            source = activeLayer.Bitmap;
            offsetX = activeLayer.OffsetX;
            offsetY = activeLayer.OffsetY;
        }

        int sourceX = (int)Math.Floor(imagePoint.X) - offsetX;
        int sourceY = (int)Math.Floor(imagePoint.Y) - offsetY;
        if (sourceX < 0 || sourceY < 0 || sourceX >= source.PixelWidth || sourceY >= source.PixelHeight)
        {
            SetStatus("마술봉은 선택 레이어 안쪽을 클릭해야 합니다.");
            return;
        }

        MagicWandSelection selection = BitmapEditor.CreateMagicWandSelectionMask(
            source,
            sourceX,
            sourceY,
            (int)MagicWandToleranceSlider.Value,
            (int)SelectionFeatherSlider.Value);

        int left = Math.Clamp(selection.Bounds.X + offsetX, 0, _currentBitmap.PixelWidth);
        int top = Math.Clamp(selection.Bounds.Y + offsetY, 0, _currentBitmap.PixelHeight);
        int right = Math.Clamp(selection.Bounds.X + selection.Bounds.Width + offsetX, 0, _currentBitmap.PixelWidth);
        int bottom = Math.Clamp(selection.Bounds.Y + selection.Bounds.Height + offsetY, 0, _currentBitmap.PixelHeight);
        if (right <= left || bottom <= top)
        {
            SetStatus("마술봉 선택 영역이 캔버스 밖에 있습니다.");
            return;
        }

        _selectionMask = BitmapEditor.PlaceMaskOnCanvas(
            selection.Mask,
            _currentBitmap.PixelWidth,
            _currentBitmap.PixelHeight,
            offsetX,
            offsetY,
            _currentBitmap.DpiX,
            _currentBitmap.DpiY);
        _selectionRect = new Int32Rect(left, top, right - left, bottom - top);
        _selectionMode = false;
        _isSelecting = false;
        UpdateSelectionOverlay();
        UpdateSelectionButton();
        SetStatus($"마술봉 선택: {selection.PixelCount:N0}px, 영역 {right - left} x {bottom - top}");
    }

    private bool TryGetSelection(out Int32Rect rect)
    {
        rect = default;
        if (_currentBitmap is null || _selectionRect is not { } selection)
        {
            return false;
        }

        int x = Math.Clamp(selection.X, 0, Math.Max(0, _currentBitmap.PixelWidth - 1));
        int y = Math.Clamp(selection.Y, 0, Math.Max(0, _currentBitmap.PixelHeight - 1));
        int right = Math.Clamp(selection.X + selection.Width, x + 1, _currentBitmap.PixelWidth);
        int bottom = Math.Clamp(selection.Y + selection.Height, y + 1, _currentBitmap.PixelHeight);
        rect = new Int32Rect(x, y, right - x, bottom - y);
        return rect.Width > 1 && rect.Height > 1;
    }

    private bool TryGetSelectionMaskForTarget(BitmapSource target, ImageLayer? layer, out BitmapSource mask)
    {
        mask = null!;
        if (!TryGetSelection(out Int32Rect rect))
        {
            return false;
        }

        if (_selectionMask is not null)
        {
            int offsetX = layer?.OffsetX ?? 0;
            int offsetY = layer?.OffsetY ?? 0;
            mask = BitmapEditor.ProjectCanvasSelectionMask(
                _selectionMask,
                target.PixelWidth,
                target.PixelHeight,
                offsetX,
                offsetY,
                target.DpiX,
                target.DpiY);
            return BitmapEditor.HasMaskCoverage(mask);
        }

        Int32Rect targetRect = layer is not null
            ? ToLayerRect(rect, layer)
            : rect;
        if (!TryClipRect(targetRect, target.PixelWidth, target.PixelHeight, out Int32Rect clippedRect))
        {
            return false;
        }

        mask = BitmapEditor.CreateSelectionMask(
            target.PixelWidth,
            target.PixelHeight,
            clippedRect,
            (int)SelectionFeatherSlider.Value,
            target.DpiX,
            target.DpiY);
        return true;
    }

    private static Int32Rect ToLayerRect(Int32Rect canvasRect, ImageLayer layer)
    {
        return new Int32Rect(
            canvasRect.X - layer.OffsetX,
            canvasRect.Y - layer.OffsetY,
            canvasRect.Width,
            canvasRect.Height);
    }

    private static bool TryClipRect(Int32Rect rect, int width, int height, out Int32Rect clippedRect)
    {
        int left = Math.Clamp(rect.X, 0, width);
        int top = Math.Clamp(rect.Y, 0, height);
        int right = Math.Clamp(rect.X + rect.Width, 0, width);
        int bottom = Math.Clamp(rect.Y + rect.Height, 0, height);
        clippedRect = right > left && bottom > top
            ? new Int32Rect(left, top, right - left, bottom - top)
            : default;
        return clippedRect.Width > 0 && clippedRect.Height > 0;
    }

    private void UpdateSelectionOverlay()
    {
        if (!TryGetSelection(out Int32Rect rect))
        {
            SelectionRect.Visibility = Visibility.Collapsed;
            SelectionMaskOverlay.Source = null;
            SelectionMaskOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        if (_selectionMask is not null)
        {
            SelectionMaskOverlay.Width = _currentBitmap!.PixelWidth;
            SelectionMaskOverlay.Height = _currentBitmap.PixelHeight;
            SelectionMaskOverlay.Source = BitmapEditor.CreateSelectionOverlay(
                _selectionMask,
                Color.FromRgb(125, 219, 210),
                0.36);
            SelectionMaskOverlay.Visibility = Visibility.Visible;
        }
        else
        {
            SelectionMaskOverlay.Source = null;
            SelectionMaskOverlay.Visibility = Visibility.Collapsed;
        }

        Canvas.SetLeft(SelectionRect, rect.X);
        Canvas.SetTop(SelectionRect, rect.Y);
        SelectionRect.Width = rect.Width;
        SelectionRect.Height = rect.Height;
        SelectionRect.Visibility = Visibility.Visible;
    }

    private void UpdateActiveLayerBounds()
    {
        if (_currentBitmap is null || GetActiveLayer() is not ImageLayer activeLayer || !activeLayer.IsVisible)
        {
            HideActiveLayerBounds();
            return;
        }

        double x = activeLayer.OffsetX;
        double y = activeLayer.OffsetY;
        double width = activeLayer.Bitmap.PixelWidth;
        double height = activeLayer.Bitmap.PixelHeight;
        if (width <= 0 || height <= 0)
        {
            HideActiveLayerBounds();
            return;
        }

        ActiveLayerBounds.Width = width;
        ActiveLayerBounds.Height = height;
        Canvas.SetLeft(ActiveLayerBounds, x);
        Canvas.SetTop(ActiveLayerBounds, y);
        PositionLayerHandle(ActiveLayerHandleTopLeft, x, y);
        PositionLayerHandle(ActiveLayerHandleTopRight, x + width, y);
        PositionLayerHandle(ActiveLayerHandleBottomLeft, x, y + height);
        PositionLayerHandle(ActiveLayerHandleBottomRight, x + width, y + height);

        ActiveLayerBounds.Visibility = Visibility.Visible;
        ActiveLayerHandleTopLeft.Visibility = Visibility.Visible;
        ActiveLayerHandleTopRight.Visibility = Visibility.Visible;
        ActiveLayerHandleBottomLeft.Visibility = Visibility.Visible;
        ActiveLayerHandleBottomRight.Visibility = Visibility.Visible;
    }

    private void HideActiveLayerBounds()
    {
        ActiveLayerBounds.Visibility = Visibility.Collapsed;
        ActiveLayerHandleTopLeft.Visibility = Visibility.Collapsed;
        ActiveLayerHandleTopRight.Visibility = Visibility.Collapsed;
        ActiveLayerHandleBottomLeft.Visibility = Visibility.Collapsed;
        ActiveLayerHandleBottomRight.Visibility = Visibility.Collapsed;
    }

    private static void PositionLayerHandle(System.Windows.Shapes.Rectangle handle, double x, double y)
    {
        Canvas.SetLeft(handle, x - (handle.Width / 2));
        Canvas.SetTop(handle, y - (handle.Height / 2));
    }

    private void ClearSelectionOverlay(bool disableMode)
    {
        _selectionRect = null;
        _selectionMask = null;
        _isSelecting = false;
        _isLassoSelecting = false;
        ClearLassoPath();
        if (disableMode)
        {
            _selectionMode = false;
            _lassoMode = false;
            _magicWandMode = false;
        }

        SelectionRect.Visibility = Visibility.Collapsed;
        SelectionMaskOverlay.Source = null;
        SelectionMaskOverlay.Visibility = Visibility.Collapsed;
        UpdateSelectionButton();
    }

    private void MoveActiveLayerTo(Point imagePoint)
    {
        if (GetActiveLayer() is not ImageLayer activeLayer)
        {
            return;
        }

        int offsetX = _layerMoveStartOffsetX + (int)Math.Round(imagePoint.X - _layerMoveStart.X);
        int offsetY = _layerMoveStartOffsetY + (int)Math.Round(imagePoint.Y - _layerMoveStart.Y);
        if (offsetX == activeLayer.OffsetX && offsetY == activeLayer.OffsetY)
        {
            return;
        }

        if (!_layerMoveCaptured)
        {
            PushUndo();
            _layerMoveCaptured = true;
        }

        SetLayerOffset(activeLayer, offsetX, offsetY);
    }

    private void BeginLayerResize(LayerResizeHandle handle, Point imagePoint)
    {
        if (GetActiveLayer() is not ImageLayer activeLayer)
        {
            return;
        }

        if (!EnsureLayerEditable(activeLayer, "변형"))
        {
            return;
        }

        _isResizingLayer = true;
        _layerResizeCaptured = false;
        _layerResizeHandle = handle;
        _layerResizeStartOffsetX = activeLayer.OffsetX;
        _layerResizeStartOffsetY = activeLayer.OffsetY;
        _layerResizeStartBitmap = activeLayer.Bitmap;
        _layerResizeStartMask = activeLayer.Mask;
        _layerResizeAnchor = handle switch
        {
            LayerResizeHandle.TopLeft => new Point(activeLayer.OffsetX + activeLayer.Bitmap.PixelWidth, activeLayer.OffsetY + activeLayer.Bitmap.PixelHeight),
            LayerResizeHandle.TopRight => new Point(activeLayer.OffsetX, activeLayer.OffsetY + activeLayer.Bitmap.PixelHeight),
            LayerResizeHandle.BottomLeft => new Point(activeLayer.OffsetX + activeLayer.Bitmap.PixelWidth, activeLayer.OffsetY),
            LayerResizeHandle.BottomRight => new Point(activeLayer.OffsetX, activeLayer.OffsetY),
            _ => imagePoint
        };

        ImageHost.Cursor = GetResizeCursor(handle);
        Mouse.Capture(ImageHost);
        SetStatus("레이어 자유 변형: 모서리를 드래그하세요. Shift를 누르면 비율이 고정됩니다.");
    }

    private void ResizeActiveLayerTo(Point imagePoint)
    {
        if (GetActiveLayer() is not ImageLayer activeLayer || _layerResizeStartBitmap is null || _layerResizeHandle == LayerResizeHandle.None)
        {
            return;
        }

        double dx = imagePoint.X - _layerResizeAnchor.X;
        double dy = imagePoint.Y - _layerResizeAnchor.Y;
        double width = Math.Abs(dx);
        double height = Math.Abs(dy);

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            double aspect = _layerResizeStartBitmap.PixelWidth / (double)Math.Max(1, _layerResizeStartBitmap.PixelHeight);
            if (width / Math.Max(1, height) > aspect)
            {
                width = Math.Max(1, height * aspect);
            }
            else
            {
                height = Math.Max(1, width / aspect);
            }

            dx = Math.Sign(dx == 0 ? 1 : dx) * width;
            dy = Math.Sign(dy == 0 ? 1 : dy) * height;
        }

        width = Math.Clamp(width, 1, 32768);
        height = Math.Clamp(height, 1, 32768);
        int newWidth = Math.Max(1, (int)Math.Round(width));
        int newHeight = Math.Max(1, (int)Math.Round(height));
        int offsetX = (int)Math.Round(Math.Min(_layerResizeAnchor.X, _layerResizeAnchor.X + dx));
        int offsetY = (int)Math.Round(Math.Min(_layerResizeAnchor.Y, _layerResizeAnchor.Y + dy));

        if (newWidth == activeLayer.Bitmap.PixelWidth
            && newHeight == activeLayer.Bitmap.PixelHeight
            && offsetX == activeLayer.OffsetX
            && offsetY == activeLayer.OffsetY)
        {
            return;
        }

        if (!_layerResizeCaptured)
        {
            PushUndo();
            _layerResizeCaptured = true;
        }

        _suppressLayerRefresh = true;
        try
        {
            activeLayer.Bitmap = BitmapEditor.Resize(_layerResizeStartBitmap, newWidth, newHeight);
            activeLayer.Mask = _layerResizeStartMask is null
                ? null
                : BitmapEditor.Resize(_layerResizeStartMask, newWidth, newHeight);
            activeLayer.OffsetX = offsetX;
            activeLayer.OffsetY = offsetY;
        }
        finally
        {
            _suppressLayerRefresh = false;
        }

        RefreshCompositeFromLayers();
        SetStatus($"레이어 자유 변형: {newWidth} x {newHeight}");
    }

    private bool TryGetLayerResizeHandle(Point imagePoint, out LayerResizeHandle handle)
    {
        handle = LayerResizeHandle.None;
        if (_currentBitmap is null || GetActiveLayer() is not ImageLayer activeLayer || !activeLayer.IsVisible || activeLayer.IsLocked)
        {
            return false;
        }

        double x = activeLayer.OffsetX;
        double y = activeLayer.OffsetY;
        double width = activeLayer.Bitmap.PixelWidth;
        double height = activeLayer.Bitmap.PixelHeight;
        double radius = Math.Max(7, 11 / Math.Max(_zoom, 0.1));
        var handles = new (LayerResizeHandle Handle, Point Point)[]
        {
            (LayerResizeHandle.TopLeft, new Point(x, y)),
            (LayerResizeHandle.TopRight, new Point(x + width, y)),
            (LayerResizeHandle.BottomLeft, new Point(x, y + height)),
            (LayerResizeHandle.BottomRight, new Point(x + width, y + height))
        };

        double bestDistance = double.MaxValue;
        foreach ((LayerResizeHandle candidate, Point point) in handles)
        {
            double distance = Distance(imagePoint, point);
            if (distance <= radius && distance < bestDistance)
            {
                handle = candidate;
                bestDistance = distance;
            }
        }

        return handle != LayerResizeHandle.None;
    }

    private void UpdateLayerHandleCursor(Point imagePoint)
    {
        if (_isPanning || _isMovingLayer || _isResizingLayer || IsBrushModeActive() || _selectionMode || _cropMode)
        {
            return;
        }

        ImageHost.Cursor = TryGetLayerResizeHandle(imagePoint, out LayerResizeHandle handle)
            ? GetResizeCursor(handle)
            : null;
    }

    private static Cursor GetResizeCursor(LayerResizeHandle handle)
    {
        return handle is LayerResizeHandle.TopLeft or LayerResizeHandle.BottomRight
            ? Cursors.SizeNWSE
            : Cursors.SizeNESW;
    }

    private void ClearLayerResizeState()
    {
        _isResizingLayer = false;
        _layerResizeCaptured = false;
        _layerResizeHandle = LayerResizeHandle.None;
        _layerResizeStartBitmap = null;
        _layerResizeStartMask = null;
    }

    private static double Distance(Point first, Point second)
    {
        double dx = first.X - second.X;
        double dy = first.Y - second.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    private void MoveActiveLayerToIndex(int targetIndex, string historyLabel, string status)
    {
        if (GetActiveLayer() is not ImageLayer activeLayer)
        {
            SetStatus("순서를 바꿀 레이어가 없습니다.");
            return;
        }

        if (_layers.Count <= 1)
        {
            SetStatus("순서를 바꿀 다른 레이어가 없습니다.");
            return;
        }

        int currentIndex = _layers.IndexOf(activeLayer);
        targetIndex = Math.Clamp(targetIndex, 0, _layers.Count - 1);
        if (currentIndex < 0 || currentIndex == targetIndex)
        {
            return;
        }

        PushUndo();
        _layers.Move(currentIndex, targetIndex);
        SelectLayer(activeLayer);
        RefreshCompositeFromLayers();
        RecordHistory(historyLabel);
        SetStatus(status);
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
            _selectionRect = null;
            _selectionMask = null;
            SelectionRect.Visibility = Visibility.Collapsed;
            SelectionMaskOverlay.Source = null;
            SelectionMaskOverlay.Visibility = Visibility.Collapsed;
            ClearLassoPath();
            GridOverlay.Visibility = Visibility.Collapsed;
            CloneSourceMarker.Visibility = Visibility.Collapsed;
            HideActiveLayerBounds();
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
            UpdateHistogramPreview();
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
        UpdateHistogramPreview();
        UpdateSelectionOverlay();
        UpdateGridOverlay();
        UpdateCloneSourceMarker();
        UpdateActiveLayerBounds();
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

    private HslSettings GetHslSettings()
    {
        return new HslSettings(
            Math.Clamp(HueShiftSlider.Value, -180, 180),
            Math.Clamp(HslSaturationSlider.Value, -100, 100),
            Math.Clamp(HslLightnessSlider.Value, -100, 100));
    }

    private void SetHslSettings(HslSettings settings)
    {
        _suppressHslUpdate = true;
        try
        {
            HueShiftSlider.Value = Math.Clamp(settings.HueShift, -180, 180);
            HslSaturationSlider.Value = Math.Clamp(settings.Saturation, -100, 100);
            HslLightnessSlider.Value = Math.Clamp(settings.Lightness, -100, 100);
        }
        finally
        {
            _suppressHslUpdate = false;
        }

        UpdateHslSummary();
    }

    private void UpdateHslSummary()
    {
        if (HueShiftText is null
            || HslSaturationText is null
            || HslLightnessText is null
            || HueShiftSlider is null
            || HslSaturationSlider is null
            || HslLightnessSlider is null)
        {
            return;
        }

        HslSettings settings = GetHslSettings();
        HueShiftText.Text = $"{settings.HueShift:F0}°";
        HslSaturationText.Text = $"{settings.Saturation:F0}";
        HslLightnessText.Text = $"{settings.Lightness:F0}";
    }

    private LevelSettings GetLevelSettings()
    {
        double inputBlack = Math.Clamp(LevelsBlackSlider.Value, 0, 254);
        double inputWhite = Math.Clamp(LevelsWhiteSlider.Value, 1, 255);
        if (inputWhite <= inputBlack)
        {
            inputWhite = Math.Min(255, inputBlack + 1);
        }

        double outputBlack = Math.Clamp(LevelsOutputBlackSlider.Value, 0, 254);
        double outputWhite = Math.Clamp(LevelsOutputWhiteSlider.Value, 1, 255);
        if (outputWhite <= outputBlack)
        {
            outputWhite = Math.Min(255, outputBlack + 1);
        }

        return new LevelSettings(
            inputBlack,
            inputWhite,
            Math.Clamp(LevelsGammaSlider.Value, 0.1, 3.0),
            outputBlack,
            outputWhite);
    }

    private void SetLevelSliders(LevelSettings settings)
    {
        _suppressLevelsUpdate = true;
        try
        {
            LevelsBlackSlider.Value = Math.Clamp(settings.InputBlack, 0, 254);
            LevelsWhiteSlider.Value = Math.Clamp(settings.InputWhite, 1, 255);
            LevelsGammaSlider.Value = Math.Clamp(settings.Gamma, 0.1, 3.0);
            LevelsOutputBlackSlider.Value = Math.Clamp(settings.OutputBlack, 0, 254);
            LevelsOutputWhiteSlider.Value = Math.Clamp(settings.OutputWhite, 1, 255);
        }
        finally
        {
            _suppressLevelsUpdate = false;
        }

        NormalizeLevelSliders();
        UpdateLevelsSummary();
    }

    private void NormalizeLevelSliders()
    {
        if (LevelsInputText is null
            || LevelsBlackSlider is null
            || LevelsWhiteSlider is null
            || LevelsOutputBlackSlider is null
            || LevelsOutputWhiteSlider is null)
        {
            return;
        }

        _suppressLevelsUpdate = true;
        try
        {
            if (LevelsWhiteSlider.Value <= LevelsBlackSlider.Value)
            {
                if (LevelsBlackSlider.Value >= 254)
                {
                    LevelsBlackSlider.Value = 254;
                    LevelsWhiteSlider.Value = 255;
                }
                else
                {
                    LevelsWhiteSlider.Value = LevelsBlackSlider.Value + 1;
                }
            }

            if (LevelsOutputWhiteSlider.Value <= LevelsOutputBlackSlider.Value)
            {
                if (LevelsOutputBlackSlider.Value >= 254)
                {
                    LevelsOutputBlackSlider.Value = 254;
                    LevelsOutputWhiteSlider.Value = 255;
                }
                else
                {
                    LevelsOutputWhiteSlider.Value = LevelsOutputBlackSlider.Value + 1;
                }
            }
        }
        finally
        {
            _suppressLevelsUpdate = false;
        }
    }

    private void UpdateLevelsSummary()
    {
        if (LevelsInputText is null
            || LevelsGammaText is null
            || LevelsOutputText is null
            || LevelsBlackSlider is null
            || LevelsWhiteSlider is null
            || LevelsGammaSlider is null
            || LevelsOutputBlackSlider is null
            || LevelsOutputWhiteSlider is null)
        {
            return;
        }

        LevelSettings settings = GetLevelSettings();
        LevelsInputText.Text = $"{settings.InputBlack:F0} - {settings.InputWhite:F0}";
        LevelsGammaText.Text = $"{settings.Gamma:F2}";
        LevelsOutputText.Text = $"{settings.OutputBlack:F0} - {settings.OutputWhite:F0}";
    }

    private void UpdateHistogramPreview()
    {
        if (HistogramPath is null)
        {
            return;
        }

        if (_currentBitmap is null)
        {
            HistogramPath.Data = null;
            return;
        }

        int[] histogram = BitmapEditor.CalculateLuminanceHistogram(_currentBitmap);
        int max = 0;
        for (int i = 0; i < histogram.Length; i++)
        {
            max = Math.Max(max, histogram[i]);
        }

        if (max <= 0)
        {
            HistogramPath.Data = null;
            return;
        }

        var geometry = new StreamGeometry();
        using (StreamGeometryContext context = geometry.Open())
        {
            context.BeginFigure(new Point(0, 1), isFilled: true, isClosed: true);
            for (int i = 0; i < histogram.Length; i++)
            {
                double x = i / 255.0;
                double normalized = histogram[i] / (double)max;
                double y = 1.0 - Math.Pow(normalized, 0.42);
                context.LineTo(new Point(x, y), isStroked: true, isSmoothJoin: false);
            }

            context.LineTo(new Point(1, 1), isStroked: true, isSmoothJoin: false);
        }

        geometry.Freeze();
        HistogramPath.Data = geometry;
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

        if (e.PropertyName is nameof(ImageLayer.IsVisible) or nameof(ImageLayer.Opacity) or nameof(ImageLayer.Bitmap) or nameof(ImageLayer.BlendMode) or nameof(ImageLayer.Mask) or nameof(ImageLayer.OffsetX) or nameof(ImageLayer.OffsetY))
        {
            RefreshCompositeFromLayers();
            UpdateLayerControls();
            return;
        }

        if (e.PropertyName is nameof(ImageLayer.Name) or nameof(ImageLayer.IsLocked))
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

    private bool EnsureActiveLayerEditable(string action)
    {
        return GetActiveLayer() is not { } activeLayer || EnsureLayerEditable(activeLayer, action);
    }

    private bool EnsureLayerEditable(ImageLayer layer, string action)
    {
        if (!layer.IsLocked)
        {
            return true;
        }

        SetStatus($"잠긴 레이어는 {action}할 수 없습니다: {layer.Name}");
        return false;
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

        if (!EnsureLayerEditable(activeLayer, "마스크 편집"))
        {
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

    private bool SetEditableBitmap(BitmapSource bitmap, bool refreshPreview = true)
    {
        if (GetActiveLayer() is ImageLayer activeLayer)
        {
            if (!EnsureLayerEditable(activeLayer, "편집"))
            {
                return false;
            }

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

            return true;
        }

        _currentBitmap = bitmap;
        if (refreshPreview)
        {
            UpdatePreview(_currentBitmap, _currentPath);
        }

        return true;
    }

    private void SetLayerMask(ImageLayer layer, BitmapSource? mask)
    {
        if (!EnsureLayerEditable(layer, "마스크 편집"))
        {
            return;
        }

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

    private void SetLayerOffset(ImageLayer layer, int offsetX, int offsetY, bool refreshPreview = true)
    {
        if (!EnsureLayerEditable(layer, "이동"))
        {
            return;
        }

        _suppressLayerRefresh = true;
        try
        {
            layer.OffsetX = offsetX;
            layer.OffsetY = offsetY;
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
    }

    private void ResizeLayerToCanvas(bool fill)
    {
        if (_currentBitmap is null || GetActiveLayer() is not ImageLayer activeLayer)
        {
            SetStatus("크기를 맞출 레이어가 없습니다.");
            return;
        }

        double scaleX = _currentBitmap.PixelWidth / (double)Math.Max(1, activeLayer.Bitmap.PixelWidth);
        double scaleY = _currentBitmap.PixelHeight / (double)Math.Max(1, activeLayer.Bitmap.PixelHeight);
        double scale = fill ? Math.Max(scaleX, scaleY) : Math.Min(scaleX, scaleY);
        int width = Math.Max(1, (int)Math.Round(activeLayer.Bitmap.PixelWidth * scale));
        int height = Math.Max(1, (int)Math.Round(activeLayer.Bitmap.PixelHeight * scale));
        ResizeActiveLayer(
            activeLayer,
            width,
            height,
            centerOnCanvas: true,
            fill ? "레이어 캔버스 채우기" : "레이어 캔버스 맞춤",
            fill ? "선택 레이어를 캔버스에 채웠습니다." : "선택 레이어를 캔버스에 맞췄습니다.");
    }

    private void ResizeActiveLayer(
        ImageLayer activeLayer,
        int width,
        int height,
        bool centerOnCanvas,
        string historyLabel,
        string status)
    {
        if (!EnsureLayerEditable(activeLayer, "크기 변경"))
        {
            return;
        }

        width = Math.Clamp(width, 1, 32768);
        height = Math.Clamp(height, 1, 32768);
        PushUndo();
        _suppressLayerRefresh = true;
        try
        {
            activeLayer.Bitmap = BitmapEditor.Resize(activeLayer.Bitmap, width, height);
            if (activeLayer.Mask is not null)
            {
                activeLayer.Mask = BitmapEditor.Resize(activeLayer.Mask, width, height);
            }

            if (centerOnCanvas && _currentBitmap is not null)
            {
                activeLayer.OffsetX = (int)Math.Round((_currentBitmap.PixelWidth - width) / 2.0);
                activeLayer.OffsetY = (int)Math.Round((_currentBitmap.PixelHeight - height) / 2.0);
            }
        }
        finally
        {
            _suppressLayerRefresh = false;
        }

        RefreshCompositeFromLayers();
        RecordHistory(historyLabel);
        SetStatus(status);
    }

    private void TransformActiveLayer(
        Func<BitmapSource, BitmapSource> transform,
        string historyLabel,
        string status,
        bool keepCenter)
    {
        if (GetActiveLayer() is not ImageLayer activeLayer)
        {
            SetStatus("변형할 레이어가 없습니다.");
            return;
        }

        if (!EnsureLayerEditable(activeLayer, "변형"))
        {
            return;
        }

        double centerX = activeLayer.OffsetX + (activeLayer.Bitmap.PixelWidth / 2.0);
        double centerY = activeLayer.OffsetY + (activeLayer.Bitmap.PixelHeight / 2.0);
        PushUndo();
        _suppressLayerRefresh = true;
        try
        {
            activeLayer.Bitmap = transform(activeLayer.Bitmap);
            if (activeLayer.Mask is not null)
            {
                activeLayer.Mask = transform(activeLayer.Mask);
            }

            if (keepCenter)
            {
                activeLayer.OffsetX = (int)Math.Round(centerX - (activeLayer.Bitmap.PixelWidth / 2.0));
                activeLayer.OffsetY = (int)Math.Round(centerY - (activeLayer.Bitmap.PixelHeight / 2.0));
            }
        }
        finally
        {
            _suppressLayerRefresh = false;
        }

        RefreshCompositeFromLayers();
        RecordHistory(historyLabel);
        SetStatus(status);
    }

    private void TransformAllLayers(
        Func<BitmapSource, BitmapSource> transform,
        Func<ImageLayer, (int OffsetX, int OffsetY)>? transformOffset = null,
        Func<BitmapSource, BitmapSource>? transformCanvas = null)
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

                if (transformOffset is not null)
                {
                    (layer.OffsetX, layer.OffsetY) = transformOffset(layer);
                }
            }

            if (_currentBitmap is not null)
            {
                _currentBitmap = (transformCanvas ?? transform)(_currentBitmap);
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

        int width = Math.Max(1, _currentBitmap?.PixelWidth ?? _layers[0].Bitmap.PixelWidth);
        int height = Math.Max(1, _currentBitmap?.PixelHeight ?? _layers[0].Bitmap.PixelHeight);
        BitmapSource first = _layers[0].Bitmap;
        var visibleLayers = _layers
            .Where(layer => layer.IsVisible)
            .Select(layer => (layer.Bitmap, layer.Opacity / 100.0, layer.BlendMode, layer.Mask, layer.OffsetX, layer.OffsetY));

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
            .Select(layer => new ImageLayerSnapshot(layer.Name, layer.Bitmap, layer.Mask, layer.OffsetX, layer.OffsetY, layer.IsVisible, layer.IsLocked, layer.Opacity, layer.BlendMode))
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
                    OffsetX = snapshot.OffsetX,
                    OffsetY = snapshot.OffsetY,
                    IsVisible = snapshot.IsVisible,
                    IsLocked = snapshot.IsLocked,
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
            bool canEditLayer = activeLayer is not null && !activeLayer.IsLocked;
            LayerOpacitySlider.IsEnabled = activeLayer is not null;
            LayerOpacitySlider.Value = activeLayer?.Opacity ?? 100;
            LayerOpacityText.Text = activeLayer is null ? "-" : $"{activeLayer.Opacity:F0}%";
            LayerLockBox.IsEnabled = activeLayer is not null;
            LayerLockBox.IsChecked = activeLayer?.IsLocked == true;
            LayerNameBox.IsEnabled = activeLayer is not null;
            LayerNameBox.Text = activeLayer?.Name ?? "";
            LayerBlendModeBox.IsEnabled = activeLayer is not null;
            SetComboByTag(LayerBlendModeBox, activeLayer?.BlendMode.ToString() ?? nameof(ImageBlendMode.Normal));
            LayerWidthBox.IsEnabled = canEditLayer;
            LayerHeightBox.IsEnabled = canEditLayer;
            LayerWidthBox.Text = activeLayer?.Bitmap.PixelWidth.ToString() ?? "";
            LayerHeightBox.Text = activeLayer?.Bitmap.PixelHeight.ToString() ?? "";
            LayerOffsetXBox.IsEnabled = canEditLayer;
            LayerOffsetYBox.IsEnabled = canEditLayer;
            LayerOffsetXBox.Text = activeLayer?.OffsetX.ToString() ?? "";
            LayerOffsetYBox.Text = activeLayer?.OffsetY.ToString() ?? "";
            MoveLayerButton.IsEnabled = canEditLayer;
        }
        finally
        {
            _suppressLayerControlUpdate = false;
        }

        UpdateActiveLayerBounds();
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

        if (_cloneStampMode)
        {
            return "복제 도장";
        }

        if (_dodgeMode)
        {
            return "닷지 브러시";
        }

        if (_burnMode)
        {
            return "번 브러시";
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
        if (!SetEditableBitmap(transform(GetEditableBitmap()!)))
        {
            return;
        }
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

        if (_cloneStampMode && _cloneSourcePoint is null)
        {
            SetStatus("복제 도장은 Alt+클릭으로 원본 위치를 먼저 찍어야 합니다.");
            return;
        }

        if (!EnsureActiveLayerEditable("브러시 편집"))
        {
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

            x -= activeLayer.OffsetX;
            y -= activeLayer.OffsetY;
            BitmapSource mask = activeLayer.Mask
                ?? BitmapEditor.CreateMask(activeLayer.Bitmap.PixelWidth, activeLayer.Bitmap.PixelHeight, 255, activeLayer.Bitmap.DpiX, activeLayer.Bitmap.DpiY);
            BitmapSource editedMask = BitmapEditor.ApplyMaskBrush(
                mask,
                x,
                y,
                radius,
                reveal: _maskRevealMode,
                MaskBrushStrengthSlider.Value / 100.0,
                MaskBrushHardnessSlider.Value / 100.0);
            SetLayerMask(activeLayer, editedMask);
            return;
        }

        int layerOffsetX = 0;
        int layerOffsetY = 0;
        if (GetActiveLayer() is ImageLayer editLayer)
        {
            layerOffsetX = editLayer.OffsetX;
            layerOffsetY = editLayer.OffsetY;
            x -= layerOffsetX;
            y -= layerOffsetY;
        }

        BitmapSource? target = GetEditableBitmap();
        if (target is null)
        {
            return;
        }

        BitmapSource edited;
        if (_cloneStampMode)
        {
            _cloneStrokeStart ??= point;
            _cloneStrokeSourceStart ??= _cloneSourcePoint!.Value;
            _cloneStrokeSourceBitmap ??= target;

            Point strokeStart = _cloneStrokeStart.Value;
            Point sourceStart = _cloneStrokeSourceStart.Value;
            int sourceX = (int)Math.Round(sourceStart.X + (point.X - strokeStart.X)) - layerOffsetX;
            int sourceY = (int)Math.Round(sourceStart.Y + (point.Y - strokeStart.Y)) - layerOffsetY;
            edited = BitmapEditor.ApplyCloneStampBrush(
                target,
                _cloneStrokeSourceBitmap,
                x,
                y,
                sourceX,
                sourceY,
                radius,
                CloneStrengthSlider.Value / 100.0,
                BrushHardnessSlider.Value / 100.0);
        }
        else if (_autoRestoreMode)
        {
            edited = BitmapEditor.ApplyAutoRestoreBrush(
                target,
                _restoreSourceBitmap!,
                x,
                y,
                radius,
                (int)AutoRestoreSensitivitySlider.Value,
                AutoRestoreStrengthSlider.Value / 100.0,
                BrushHardnessSlider.Value / 100.0);
        }
        else if (_dodgeMode || _burnMode)
        {
            edited = BitmapEditor.ApplyDodgeBurnBrush(
                target,
                x,
                y,
                radius,
                dodge: _dodgeMode,
                DodgeBurnStrengthSlider.Value / 100.0,
                BrushHardnessSlider.Value / 100.0);
        }
        else
        {
            edited = BitmapEditor.ApplyAlphaBrush(
                target,
                x,
                y,
                radius,
                restore: _restoreMode,
                BrushHardnessSlider.Value / 100.0);
        }

        SetEditableBitmap(edited, refreshPreview: false);
    }

    private void UpdateBrushGhost(Point imagePoint)
    {
        if (!IsBrushModeActive())
        {
            BrushGhost.Visibility = Visibility.Collapsed;
            return;
        }

        bool restorativeBrush = _restoreMode || _autoRestoreMode || _maskRevealMode || _cloneStampMode || _dodgeMode;
        bool warmBrush = _burnMode || _eraseMode || _maskHideMode;
        bool maskBrush = _maskHideMode || _maskRevealMode;
        double size = (maskBrush ? MaskBrushSizeSlider.Value : BrushSizeSlider.Value) * 2;
        double hardness = maskBrush ? MaskBrushHardnessSlider.Value : BrushHardnessSlider.Value;
        byte fillAlpha = (byte)Math.Clamp(20 + (hardness * 0.45), 20, 70);
        BrushGhost.Width = size;
        BrushGhost.Height = size;
        BrushGhost.Stroke = restorativeBrush ? FindBrush("AccentBrush") : FindBrush("AccentWarmBrush");
        BrushGhost.StrokeThickness = hardness > 82 ? 2.6 : 2;
        BrushGhost.Fill = new SolidColorBrush(warmBrush
            ? Color.FromArgb(fillAlpha, 255, 184, 107)
            : Color.FromArgb(fillAlpha, 94, 234, 212));
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

    private void ClearCloneStroke()
    {
        _cloneStrokeStart = null;
        _cloneStrokeSourceStart = null;
        _cloneStrokeSourceBitmap = null;
    }

    private bool IsBrushModeActive()
    {
        return _eraseMode
            || _restoreMode
            || _autoRestoreMode
            || _cloneStampMode
            || _dodgeMode
            || _burnMode
            || _maskHideMode
            || _maskRevealMode;
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

    private void UpdateCloneSourceMarker()
    {
        if (!_cloneStampMode || _currentBitmap is null || _cloneSourcePoint is not { } sourcePoint)
        {
            CloneSourceMarker.Visibility = Visibility.Collapsed;
            return;
        }

        double size = Math.Clamp(BrushSizeSlider.Value * 0.55, 18, 54);
        CloneSourceMarker.Width = size;
        CloneSourceMarker.Height = size;
        Canvas.SetLeft(CloneSourceMarker, sourcePoint.X - (size / 2));
        Canvas.SetTop(CloneSourceMarker, sourcePoint.Y - (size / 2));
        CloneSourceMarker.Visibility = Visibility.Visible;
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
        UpdateSelectionButton();
        UpdateMoveLayerButton();
        SetStatus(_eraseMode ? "지우개 브러시 활성화"
            : _restoreMode ? "복원 브러시 활성화"
            : _autoRestoreMode ? "자동 복원 브러시 활성화: 전경으로 판단되는 부분만 복구"
            : _cloneStampMode ? (_cloneSourcePoint is null ? "복제 도장 활성화: Alt+클릭으로 원본 위치를 찍으세요." : "복제 도장 활성화: 드래그하면 샘플 위치를 따라 복제")
            : _dodgeMode ? "닷지 브러시 활성화: 드래그하면 밝게 보정합니다."
            : _burnMode ? "번 브러시 활성화: 드래그하면 어둡게 보정합니다."
            : _maskHideMode ? "레이어 마스크 숨김 브러시 활성화"
            : _maskRevealMode ? "레이어 마스크 복원 브러시 활성화"
            : "브러시 해제");
    }

    private void UpdateBrushButtons()
    {
        EraserButton.Background = _eraseMode ? FindBrush("AccentWarmBrush") : FindBrush("PanelLiftBrush");
        RestoreButton.Background = _restoreMode ? FindBrush("AccentBrush") : FindBrush("PanelLiftBrush");
        AutoRestoreButton.Background = _autoRestoreMode ? FindBrush("AccentBrush") : FindBrush("PanelLiftBrush");
        CloneStampButton.Background = _cloneStampMode ? FindBrush("AccentBrush") : FindBrush("PanelLiftBrush");
        DodgeButton.Background = _dodgeMode ? FindBrush("AccentBrush") : FindBrush("PanelLiftBrush");
        BurnButton.Background = _burnMode ? FindBrush("AccentWarmBrush") : FindBrush("PanelLiftBrush");
        MaskHideButton.Background = _maskHideMode ? FindBrush("AccentWarmBrush") : FindBrush("PanelLiftBrush");
        MaskRevealButton.Background = _maskRevealMode ? FindBrush("AccentBrush") : FindBrush("PanelLiftBrush");
        UpdateCloneSourceMarker();
    }

    private void UpdateSelectionButton()
    {
        SelectionButton.Background = _selectionMode ? FindBrush("AccentBrush") : FindBrush("PanelLiftBrush");
        SelectionButton.Foreground = _selectionMode ? new SolidColorBrush(Color.FromRgb(7, 19, 17)) : FindBrush("InkBrush");
        LassoButton.Background = _lassoMode ? FindBrush("AccentBrush") : FindBrush("PanelLiftBrush");
        LassoButton.Foreground = _lassoMode ? new SolidColorBrush(Color.FromRgb(7, 19, 17)) : FindBrush("InkBrush");
        MagicWandButton.Background = _magicWandMode ? FindBrush("AccentBrush") : FindBrush("PanelLiftBrush");
        MagicWandButton.Foreground = _magicWandMode ? new SolidColorBrush(Color.FromRgb(7, 19, 17)) : FindBrush("InkBrush");
    }

    private void UpdateMoveLayerButton()
    {
        MoveLayerButton.Background = _moveLayerMode ? FindBrush("AccentBrush") : FindBrush("PanelLiftBrush");
        MoveLayerButton.Foreground = _moveLayerMode ? new SolidColorBrush(Color.FromRgb(7, 19, 17)) : FindBrush("InkBrush");
    }

    private bool NudgeActiveLayer(int dx, int dy, bool recordUndo)
    {
        if (GetActiveLayer() is not ImageLayer activeLayer)
        {
            SetStatus("이동할 레이어가 없습니다.");
            return false;
        }

        if (!EnsureLayerEditable(activeLayer, "이동"))
        {
            return false;
        }

        if (recordUndo)
        {
            PushUndo();
        }

        SetLayerOffset(activeLayer, activeLayer.OffsetX + dx, activeLayer.OffsetY + dy);
        if (recordUndo)
        {
            RecordHistory("레이어 미세 이동");
        }

        SetStatus($"레이어 위치: X {activeLayer.OffsetX}, Y {activeLayer.OffsetY}");
        return true;
    }

    private static bool IsTextInputFocused()
    {
        return Keyboard.FocusedElement is TextBox or ComboBox;
    }

    private void ResetInteractionModes()
    {
        _cropMode = false;
        _isCropping = false;
        _selectionMode = false;
        _lassoMode = false;
        _isLassoSelecting = false;
        _magicWandMode = false;
        _isSelecting = false;
        _selectionRect = null;
        _selectionMask = null;
        _moveLayerMode = false;
        _isMovingLayer = false;
        _layerMoveCaptured = false;
        ClearLayerResizeState();
        _pickChroma = false;
        _eraseMode = false;
        _restoreMode = false;
        _autoRestoreMode = false;
        _cloneStampMode = false;
        _dodgeMode = false;
        _burnMode = false;
        _maskHideMode = false;
        _maskRevealMode = false;
        _lastBrushPoint = null;
        _cloneSourcePoint = null;
        ClearCloneStroke();
        CropRect.Visibility = Visibility.Collapsed;
        SelectionRect.Visibility = Visibility.Collapsed;
        SelectionMaskOverlay.Source = null;
        SelectionMaskOverlay.Visibility = Visibility.Collapsed;
        ClearLassoPath();
        BrushGhost.Visibility = Visibility.Collapsed;
        CloneSourceMarker.Visibility = Visibility.Collapsed;
        CropButton.Background = FindBrush("PanelLiftBrush");
        UpdateBrushButtons();
        UpdateSelectionButton();
        UpdateMoveLayerButton();
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

    private enum LayerResizeHandle
    {
        None,
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight
    }
}
