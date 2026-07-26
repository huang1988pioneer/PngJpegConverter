using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ImageMagick;
using SkiaSharp;

namespace PngToJpegConverter;

public partial class MainWindow : Window
{
    private static readonly FilePickerFileType ImageFileType = new("圖片檔案")
    {
        Patterns = new[]
        {
            "*.png", "*.jpg", "*.jpeg", "*.webp", "*.avif",
            "*.bmp", "*.gif", "*.tif", "*.tiff", "*.heic", "*.heif"
        },
        AppleUniformTypeIdentifiers = new[] { "public.image" },
        MimeTypes = new[] { "image/*" }
    };

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp", ".avif", ".bmp", ".gif",
        ".tif", ".tiff", ".heic", ".heif"
    };

    private readonly ObservableCollection<ImageListItem> _items = new();
    private readonly ObservableCollection<HistoryEntry> _history = new();
    private readonly List<string> _temporaryFiles = new();
    private string? _outputFolderPath;
    private bool _isInitialized;
    private bool _isConverting;
    private CancellationTokenSource? _convertCts;
    private ConversionMode _mode = ConversionMode.PngToJpeg;

    public MainWindow()
    {
        InitializeComponent();
        FileListControl.ItemsSource = _items;
        HistoryListControl.ItemsSource = _history;
        _isInitialized = true;
        UpdateModeUi();
        UpdateQualityText();
        RefreshSelectionState();
        UpdateProgress(0, 0, 0);
    }

    // ── Navigation ──────────────────────────────────────────────

    private void NavConvertButton_Click(object? sender, RoutedEventArgs e) => ShowView(ViewPage.Convert);
    private void NavHistoryButton_Click(object? sender, RoutedEventArgs e) => ShowView(ViewPage.History);
    private void NavSettingsButton_Click(object? sender, RoutedEventArgs e) => ShowView(ViewPage.Settings);
    private void NavAboutButton_Click(object? sender, RoutedEventArgs e) => ShowView(ViewPage.About);

    private void ShowView(ViewPage page)
    {
        ConvertView.IsVisible = page == ViewPage.Convert;
        HistoryView.IsVisible = page == ViewPage.History;
        SettingsView.IsVisible = page == ViewPage.Settings;
        AboutView.IsVisible = page == ViewPage.About;

        SetNavActive(NavConvertButton, page == ViewPage.Convert);
        SetNavActive(NavHistoryButton, page == ViewPage.History);
        SetNavActive(NavSettingsButton, page == ViewPage.Settings);
        SetNavActive(NavAboutButton, page == ViewPage.About);

        if (page == ViewPage.History)
        {
            HistoryEmptyText.IsVisible = _history.Count == 0;
        }
    }

    private static void SetNavActive(Button button, bool active)
    {
        if (active)
        {
            if (!button.Classes.Contains("active"))
            {
                button.Classes.Add("active");
            }
        }
        else
        {
            button.Classes.Remove("active");
        }
    }

    // ── Conversion mode cards ───────────────────────────────────

    private void ModePngToJpegCard_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_isConverting) return;
        _mode = ConversionMode.PngToJpeg;
        OutputFormatComboBox.SelectedIndex = 0;
        UpdateModeUi();
    }

    private void ModeJpegToPngCard_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_isConverting) return;
        _mode = ConversionMode.JpegToPng;
        OutputFormatComboBox.SelectedIndex = 1;
        UpdateModeUi();
    }

    private void UpdateModeUi()
    {
        var pngToJpeg = _mode == ConversionMode.PngToJpeg;

        if (pngToJpeg)
        {
            if (!ModePngToJpegCard.Classes.Contains("selected"))
                ModePngToJpegCard.Classes.Add("selected");
            ModeJpegToPngCard.Classes.Remove("selected");

            ModePngToJpegRadio.Stroke = Brush.Parse("#2F6BFF");
            ModePngToJpegRadio.StrokeThickness = 5;
            ModeJpegToPngRadio.Stroke = Brush.Parse("#C5CDD9");
            ModeJpegToPngRadio.StrokeThickness = 1.5;
        }
        else
        {
            if (!ModeJpegToPngCard.Classes.Contains("selected"))
                ModeJpegToPngCard.Classes.Add("selected");
            ModePngToJpegCard.Classes.Remove("selected");

            ModeJpegToPngRadio.Stroke = Brush.Parse("#2F6BFF");
            ModeJpegToPngRadio.StrokeThickness = 5;
            ModePngToJpegRadio.Stroke = Brush.Parse("#C5CDD9");
            ModePngToJpegRadio.StrokeThickness = 1.5;
        }

        var isJpeg = GetSelectedOutputFormat() == OutputFormat.Jpeg;
        QualitySlider.IsEnabled = isJpeg && !_isConverting;
        QualityTitleText.Opacity = isJpeg ? 1 : 0.45;
        QualityText.Opacity = isJpeg ? 1 : 0.45;
        UpdateQualityText();
    }

    // ── File / folder pickers ───────────────────────────────────

    private async void ChooseFilesButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_isConverting) return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "選擇圖片檔案",
            AllowMultiple = true,
            FileTypeFilter = new[] { ImageFileType }
        });

        var paths = files
            .Select(f => f.Path.LocalPath)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Cast<string>();

        await AddLocalSourcesAsync(paths);
    }

    private async void ChooseInputFolderButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_isConverting) return;

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "選擇輸入資料夾",
            AllowMultiple = false
        });

        var folder = folders.FirstOrDefault();
        if (folder?.Path.LocalPath is not { Length: > 0 } folderPath)
        {
            return;
        }

        await AddFolderAsync(folderPath);
    }

    private async void ChooseOutputFolderButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_isConverting) return;

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "選擇輸出資料夾",
            AllowMultiple = false
        });

        var folder = folders.FirstOrDefault();
        if (folder?.Path.LocalPath is not { Length: > 0 } folderPath)
        {
            return;
        }

        _outputFolderPath = folderPath;
        SetOutputFolderDisplay(folderPath);
        ShowStatus("", isError: false);
    }

    private void SetOutputFolderDisplay(string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            OutputFolderTextBox.Text = "";
            ToolTip.SetTip(OutputFolderTextBox, "未選擇時輸出到原圖資料夾");
            return;
        }

        // Always store and show the absolute full path.
        var fullPath = Path.GetFullPath(folderPath);
        _outputFolderPath = fullPath;
        OutputFolderTextBox.Text = fullPath;
        ToolTip.SetTip(OutputFolderTextBox, fullPath);
    }

    private void OpenOutputFolderButton_Click(object? sender, RoutedEventArgs e)
    {
        var path = _outputFolderPath;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            ShowStatus("尚未設定有效的輸出資料夾。", isError: true);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ShowStatus($"無法開啟資料夾：{ex.Message}", isError: true);
        }
    }

    private async void CopyOutputPathButton_Click(object? sender, RoutedEventArgs e)
    {
        var path = _outputFolderPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            ShowStatus("尚未設定輸出資料夾，沒有路徑可複製。", isError: true);
            return;
        }

        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null)
            {
                ShowStatus("無法存取剪貼簿。", isError: true);
                return;
            }

            await clipboard.SetTextAsync(path);
            ShowStatus($"已複製完整路徑：{path}", isError: false);
        }
        catch (Exception ex)
        {
            ShowStatus($"複製失敗：{ex.Message}", isError: true);
        }
    }

    private void ClearButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_isConverting) return;
        ClearSelection();
    }

    private void RemoveItemButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_isConverting) return;
        if (sender is not Button { Tag: ImageListItem item }) return;

        _items.Remove(item);
        RefreshSelectionState();
    }

    // ── Drag & drop ─────────────────────────────────────────────

    private void DropZone_DragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(DataFormats.Files)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void DropZone_Drop(object? sender, DragEventArgs e)
    {
        if (_isConverting) return;

        if (!e.Data.Contains(DataFormats.Files))
        {
            return;
        }

        var files = e.Data.GetFiles()?.ToList();
        if (files is null || files.Count == 0)
        {
            return;
        }

        var paths = new List<string>();
        foreach (var item in files)
        {
            var path = item.Path.LocalPath;
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            if (Directory.Exists(path))
            {
                await AddFolderAsync(path);
            }
            else if (File.Exists(path))
            {
                paths.Add(path);
            }
        }

        if (paths.Count > 0)
        {
            await AddLocalSourcesAsync(paths);
        }
    }

    // ── Convert / Cancel ────────────────────────────────────────

    private async void ConvertButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_items.Count == 0 || _isConverting)
        {
            return;
        }

        await ConvertAllAsync();
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        _convertCts?.Cancel();
        ShowStatus("正在取消…", isError: false);
    }

    // ── Quality / format UI ─────────────────────────────────────

    private void QualitySlider_PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (_isInitialized && e.Property == Slider.ValueProperty)
        {
            UpdateQualityText();
        }
    }

    private void DefaultQualitySlider_PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (_isInitialized && e.Property == Slider.ValueProperty)
        {
            var q = (int)Math.Round(DefaultQualitySlider.Value);
            DefaultQualityText.Text = $"{q}%";
            if (!_isConverting)
            {
                QualitySlider.Value = q;
                UpdateQualityText();
            }
        }
    }

    private void OutputFormatComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_isInitialized) return;

        _mode = GetSelectedOutputFormat() == OutputFormat.Jpeg
            ? ConversionMode.PngToJpeg
            : ConversionMode.JpegToPng;
        UpdateModeUi();
    }

    private void UpdateQualityText()
    {
        QualityText.Text = GetSelectedOutputFormat() == OutputFormat.Jpeg
            ? $"{(int)Math.Round(QualitySlider.Value)}%"
            : "—";
    }

    // ── Source management ───────────────────────────────────────

    private async Task AddFolderAsync(string folderPath)
    {
        var searchOption = IncludeSubfoldersCheckBox.IsChecked == true
            ? SearchOption.AllDirectories
            : SearchOption.TopDirectoryOnly;

        try
        {
            var paths = Directory.EnumerateFiles(folderPath, "*.*", searchOption)
                .Where(IsSupportedImagePath);
            await AddLocalSourcesAsync(paths);
        }
        catch (Exception ex)
        {
            ShowStatus($"無法讀取資料夾：{ex.Message}", isError: true);
        }
    }

    private async Task AddLocalSourcesAsync(IEnumerable<string> paths)
    {
        var existing = new HashSet<string>(_items.Select(i => i.Identity), StringComparer.OrdinalIgnoreCase);
        var added = 0;
        var skipped = 0;

        foreach (var path in paths)
        {
            if (!File.Exists(path) || !IsSupportedImagePath(path) || !existing.Add(path))
            {
                if (File.Exists(path) && IsSupportedImagePath(path))
                {
                    skipped++;
                }
                continue;
            }

            try
            {
                var info = GetImageInfo(path);
                var fi = new FileInfo(path);
                var item = new ImageListItem
                {
                    FilePath = path,
                    Identity = path,
                    FileName = Path.GetFileName(path),
                    FormatLabel = GetFormatLabel(path),
                    SizeLabel = FormatFileSize(fi.Length),
                    Status = ConvertStatus.Pending,
                    Width = info.Width,
                    Height = info.Height,
                    IsUrlSource = false,
                    OutputBaseName = Path.GetFileName(path)
                };

                _items.Add(item);
                added++;

                // Load thumbnail off critical path
                _ = LoadThumbnailAsync(item);
            }
            catch
            {
                existing.Remove(path);
            }
        }

        RefreshSelectionState();

        if (added == 0 && _items.Count == 0)
        {
            ShowStatus("沒有找到可支援的圖片檔案。", isError: true);
        }
        else if (added == 0)
        {
            ShowStatus(skipped > 0 ? "沒有新增檔案，可能已在清單中。" : "沒有新增可支援的圖片。", isError: false);
        }
        else
        {
            ShowStatus($"已新增 {added:N0} 個圖片檔案。", isError: false);
        }

        await Task.CompletedTask;
    }

    private async Task LoadThumbnailAsync(ImageListItem item)
    {
        try
        {
            var thumb = await Task.Run(() => CreateThumbnail(item.FilePath, 72));
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_items.Contains(item))
                {
                    item.Thumbnail = thumb;
                }
            });
        }
        catch
        {
            // Thumbnail is optional.
        }
    }

    private static Bitmap? CreateThumbnail(string path, int maxSize)
    {
        try
        {
            using var input = File.OpenRead(path);
            using var codec = SKCodec.Create(input);
            if (codec is null)
            {
                return CreateThumbnailWithMagick(path, maxSize);
            }

            var info = codec.Info;
            var scale = Math.Min((float)maxSize / info.Width, (float)maxSize / info.Height);
            scale = Math.Min(scale, 1f);
            var w = Math.Max(1, (int)(info.Width * scale));
            var h = Math.Max(1, (int)(info.Height * scale));

            using var bitmap = SKBitmap.Decode(path);
            if (bitmap is null)
            {
                return CreateThumbnailWithMagick(path, maxSize);
            }

            using var resized = bitmap.Resize(new SKImageInfo(w, h), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
            if (resized is null) return null;

            using var image = SKImage.FromBitmap(resized);
            using var data = image.Encode(SKEncodedImageFormat.Png, 90);
            if (data is null) return null;

            using var ms = new MemoryStream();
            data.SaveTo(ms);
            ms.Position = 0;
            return new Bitmap(ms);
        }
        catch
        {
            return CreateThumbnailWithMagick(path, maxSize);
        }
    }

    private static Bitmap? CreateThumbnailWithMagick(string path, int maxSize)
    {
        try
        {
            using var image = new MagickImage(path);
            image.AutoOrient();
            image.Thumbnail((uint)maxSize, (uint)maxSize);
            image.Format = MagickFormat.Png;
            using var ms = new MemoryStream();
            image.Write(ms);
            ms.Position = 0;
            return new Bitmap(ms);
        }
        catch
        {
            return null;
        }
    }

    // ── Conversion pipeline ─────────────────────────────────────

    private async Task ConvertAllAsync()
    {
        _convertCts = new CancellationTokenSource();
        var token = _convertCts.Token;
        _isConverting = true;
        SetBusy(true);

        var outputFormat = GetSelectedOutputFormat();
        var quality = (int)Math.Round(QualitySlider.Value);
        var usedOutputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var completed = 0;
        var failed = 0;
        var total = _items.Count;
        var cancelled = false;

        foreach (var item in _items)
        {
            item.Status = ConvertStatus.Pending;
        }

        UpdateProgress(0, 0, total);
        ShowStatus("開始轉換…", isError: false);

        try
        {
            for (var i = 0; i < _items.Count; i++)
            {
                if (token.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }

                var item = _items[i];
                item.Status = ConvertStatus.Converting;
                UpdateProgress(completed + failed, completed + failed, total);

                try
                {
                    var outputPath = GetOutputPath(item, outputFormat, usedOutputs);
                    await Task.Run(() => ConvertImage(item.FilePath, outputPath, outputFormat, quality), token);
                    item.Status = ConvertStatus.Done;
                    completed++;
                }
                catch (OperationCanceledException)
                {
                    item.Status = ConvertStatus.Pending;
                    cancelled = true;
                    break;
                }
                catch
                {
                    item.Status = ConvertStatus.Failed;
                    failed++;
                }

                var done = completed + failed;
                UpdateProgress(done, done, total);
            }

            if (cancelled)
            {
                ShowStatus($"已取消：成功 {completed:N0}，失敗 {failed:N0}，剩餘 {total - completed - failed:N0}。", isError: false);
            }
            else if (failed == 0)
            {
                ShowStatus($"完成：已轉換 {completed:N0} 個檔案。", isError: false);
            }
            else
            {
                ShowStatus($"完成：成功 {completed:N0} 個，失敗 {failed:N0} 個。", isError: true);
            }

            _history.Insert(0, new HistoryEntry
            {
                Time = DateTime.Now,
                ModeLabel = outputFormat == OutputFormat.Jpeg ? "→ JPEG" : "→ PNG",
                SuccessCount = completed,
                FailCount = failed,
                OutputFolder = _outputFolderPath ?? "（原圖資料夾）"
            });

            if (!cancelled &&
                OpenFolderAfterConvertCheckBox.IsChecked == true &&
                completed > 0 &&
                !string.IsNullOrWhiteSpace(_outputFolderPath) &&
                Directory.Exists(_outputFolderPath))
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = _outputFolderPath,
                        UseShellExecute = true
                    });
                }
                catch
                {
                    // Ignore open-folder failures after convert.
                }
            }
        }
        finally
        {
            _isConverting = false;
            _convertCts?.Dispose();
            _convertCts = null;
            SetBusy(false);
        }
    }

    private void UpdateProgress(int done, int numerator, int total)
    {
        var percent = total == 0 ? 0 : (int)Math.Round(100.0 * done / total);
        ConvertProgressBar.Value = percent;
        ProgressPercentText.Text = $"{percent}%";
        ProgressDetailText.Text = $"{numerator} / {total}";
    }

    // ── Image conversion (Skia / Magick) ────────────────────────

    private static void ConvertImage(string sourcePath, string outputPath, OutputFormat outputFormat, int quality)
    {
        if (outputFormat == OutputFormat.Png)
        {
            ConvertImageToPng(sourcePath, outputPath);
            return;
        }

        ConvertImageToJpeg(sourcePath, outputPath, quality);
    }

    private static void ConvertImageToJpeg(string sourcePath, string outputPath, int quality)
    {
        try
        {
            using var input = File.OpenRead(sourcePath);
            using var bitmap = SKBitmap.Decode(input);
            if (bitmap is null)
            {
                throw new InvalidOperationException("無法使用 SkiaSharp 讀取圖片。");
            }

            using var surface = SKSurface.Create(new SKImageInfo(
                bitmap.Width, bitmap.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
            var canvas = surface.Canvas;
            canvas.Clear(SKColors.White);
            canvas.DrawBitmap(bitmap, 0, 0);
            canvas.Flush();

            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, Math.Clamp(quality, 1, 100));
            if (data is null)
            {
                throw new InvalidOperationException("無法建立 JPEG 圖片。");
            }

            EnsureOutputDirectory(outputPath);
            using var output = File.Open(outputPath, FileMode.Create, FileAccess.Write);
            data.SaveTo(output);
        }
        catch
        {
            ConvertImageToJpegWithMagick(sourcePath, outputPath, quality);
        }
    }

    private static void ConvertImageToPng(string sourcePath, string outputPath)
    {
        try
        {
            using var input = File.OpenRead(sourcePath);
            using var bitmap = SKBitmap.Decode(input);
            if (bitmap is null)
            {
                throw new InvalidOperationException("無法使用 SkiaSharp 讀取圖片。");
            }

            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            if (data is null)
            {
                throw new InvalidOperationException("無法建立 PNG 圖片。");
            }

            EnsureOutputDirectory(outputPath);
            using var output = File.Open(outputPath, FileMode.Create, FileAccess.Write);
            data.SaveTo(output);
        }
        catch
        {
            ConvertImageToPngWithMagick(sourcePath, outputPath);
        }
    }

    private static void ConvertImageToPngWithMagick(string sourcePath, string outputPath)
    {
        try
        {
            using var image = new MagickImage(sourcePath);
            image.AutoOrient();
            image.Format = MagickFormat.Png;
            EnsureOutputDirectory(outputPath);
            image.Write(outputPath);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"無法轉換此圖片格式：{ex.Message}");
        }
    }

    private static void ConvertImageToJpegWithMagick(string sourcePath, string outputPath, int quality)
    {
        try
        {
            using var image = new MagickImage(sourcePath);
            image.AutoOrient();
            image.BackgroundColor = MagickColors.White;
            image.Alpha(AlphaOption.Remove);
            image.Format = MagickFormat.Jpeg;
            image.Quality = (uint)Math.Clamp(quality, 1, 100);
            EnsureOutputDirectory(outputPath);
            image.Write(outputPath);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"無法轉換此圖片格式：{ex.Message}");
        }
    }

    private static void EnsureOutputDirectory(string outputPath)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private string GetOutputPath(ImageListItem source, OutputFormat outputFormat, ISet<string> usedOutputs)
    {
        var directory = _outputFolderPath ??
            (source.IsUrlSource ? AppContext.BaseDirectory : Path.GetDirectoryName(source.FilePath)) ??
            AppContext.BaseDirectory;

        var fileName = SanitizeFileName(Path.GetFileNameWithoutExtension(source.OutputBaseName));
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = "converted-image";
        }

        var extension = outputFormat == OutputFormat.Png ? ".png" : ".jpg";
        var candidate = Path.Combine(directory, $"{fileName}{extension}");

        if (!File.Exists(candidate) && usedOutputs.Add(candidate))
        {
            return candidate;
        }

        var index = 1;
        while (true)
        {
            var numbered = Path.Combine(directory, $"{fileName}-{index}{extension}");
            if (!File.Exists(numbered) && usedOutputs.Add(numbered))
            {
                return numbered;
            }

            index++;
        }
    }

    // ── UI state helpers ────────────────────────────────────────

    private void RefreshSelectionState()
    {
        SourceSummaryText.Text = _items.Count == 0
            ? "尚未選擇檔案"
            : $"已選擇 {_items.Count:N0} 個檔案";
        ConvertButton.IsEnabled = _items.Count > 0 && !_isConverting;
        UpdateProgress(0, 0, _items.Count);
    }

    private void ClearSelection()
    {
        _items.Clear();
        RefreshSelectionState();
        ShowStatus("", isError: false);
        DeleteTemporaryFiles();
    }

    private void SetBusy(bool isBusy)
    {
        ConvertButton.IsEnabled = !isBusy && _items.Count > 0;
        CancelButton.IsEnabled = isBusy;
        OutputFormatComboBox.IsEnabled = !isBusy;
        QualitySlider.IsEnabled = !isBusy && GetSelectedOutputFormat() == OutputFormat.Jpeg;
    }

    private void ShowStatus(string message, bool isError)
    {
        StatusText.Foreground = isError
            ? Brush.Parse("#E04B4B")
            : Brush.Parse("#2E9B5A");
        StatusText.Text = message;
    }

    private OutputFormat GetSelectedOutputFormat()
    {
        return OutputFormatComboBox.SelectedIndex == 1 ? OutputFormat.Png : OutputFormat.Jpeg;
    }

    private static bool IsSupportedImagePath(string path)
    {
        var ext = Path.GetExtension(path);
        return !string.IsNullOrEmpty(ext) && SupportedExtensions.Contains(ext);
    }

    private static string GetFormatLabel(string path)
    {
        var ext = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        return ext switch
        {
            "JPG" => "JPEG",
            "JPEG" => "JPEG",
            "TIF" => "TIFF",
            "TIFF" => "TIFF",
            "HEIF" => "HEIC",
            _ => ext
        };
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.##} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):0.##} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):0.##} GB";
    }

    private static ImageSize GetImageInfo(string path)
    {
        try
        {
            using var codec = SKCodec.Create(path);
            if (codec is not null)
            {
                return new ImageSize(codec.Info.Width, codec.Info.Height);
            }
        }
        catch
        {
            // Fall through.
        }

        try
        {
            using var image = new MagickImage(path);
            return new ImageSize((int)image.Width, (int)image.Height);
        }
        catch
        {
            throw new InvalidOperationException("這不是有效的圖片，或是不支援的圖片格式。");
        }
    }

    private static string SanitizeFileName(string fileName)
    {
        foreach (var character in Path.GetInvalidFileNameChars())
        {
            fileName = fileName.Replace(character, '_');
        }

        return fileName.Trim();
    }

    private void DeleteTemporaryFiles()
    {
        foreach (var path in _temporaryFiles)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // Best effort.
            }
        }

        _temporaryFiles.Clear();
    }

    protected override void OnClosed(EventArgs e)
    {
        _convertCts?.Cancel();
        DeleteTemporaryFiles();
        base.OnClosed(e);
    }

    private enum ViewPage { Convert, History, Settings, About }
    private enum ConversionMode { PngToJpeg, JpegToPng }
    private enum OutputFormat { Jpeg, Png }

    private readonly record struct ImageSize(int Width, int Height);
}

public enum ConvertStatus
{
    Pending,
    Converting,
    Done,
    Failed
}

public sealed class ImageListItem : INotifyPropertyChanged
{
    private ConvertStatus _status = ConvertStatus.Pending;
    private Bitmap? _thumbnail;

    public string FilePath { get; set; } = "";
    public string Identity { get; set; } = "";
    public string FileName { get; set; } = "";
    public string FormatLabel { get; set; } = "";
    public string SizeLabel { get; set; } = "";
    public string OutputBaseName { get; set; } = "";
    public bool IsUrlSource { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    public Bitmap? Thumbnail
    {
        get => _thumbnail;
        set
        {
            if (_thumbnail != value)
            {
                _thumbnail = value;
                OnPropertyChanged();
            }
        }
    }

    public ConvertStatus Status
    {
        get => _status;
        set
        {
            if (_status != value)
            {
                _status = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusLabel));
                OnPropertyChanged(nameof(StatusBrush));
            }
        }
    }

    public string StatusLabel => Status switch
    {
        ConvertStatus.Pending => "待轉換",
        ConvertStatus.Converting => "轉換中",
        ConvertStatus.Done => "已完成",
        ConvertStatus.Failed => "失敗",
        _ => "—"
    };

    public IBrush StatusBrush => Status switch
    {
        ConvertStatus.Pending => Brush.Parse("#7A8699"),
        ConvertStatus.Converting => Brush.Parse("#2F6BFF"),
        ConvertStatus.Done => Brush.Parse("#2E9B5A"),
        ConvertStatus.Failed => Brush.Parse("#E04B4B"),
        _ => Brush.Parse("#7A8699")
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public sealed class HistoryEntry
{
    public DateTime Time { get; set; }
    public string ModeLabel { get; set; } = "";
    public int SuccessCount { get; set; }
    public int FailCount { get; set; }
    public string OutputFolder { get; set; } = "";

    public string TimeLabel => Time.ToString("yyyy-MM-dd HH:mm:ss");
    public string ResultLabel => $"{SuccessCount} / {FailCount}";
}
