using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using ImageMagick;
using SkiaSharp;

namespace PngToJpegConverter;

public partial class MainWindow : Window
{
    private static readonly FilePickerFileType ImageFileType = new("圖片檔案")
    {
        Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.webp", "*.avif", "*.bmp", "*.gif", "*.tif", "*.tiff", "*.heic", "*.heif" },
        AppleUniformTypeIdentifiers = new[] { "public.image" },
        MimeTypes = new[] { "image/*" }
    };

    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    private readonly List<SourceImage> _sources = new();
    private readonly List<string> _temporaryFiles = new();
    private string? _inputFolderPath;
    private string? _outputFolderPath;
    private bool _isInitialized;

    public MainWindow()
    {
        InitializeComponent();
        _isInitialized = true;
        UpdateQualityText();
        UpdateOutputFormatUi();
        RefreshSelectionState();
    }

    private async void ChooseFilesButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "選擇圖片檔案",
            AllowMultiple = true,
            FileTypeFilter = new[] { ImageFileType }
        });

        var paths = files
            .Select(file => file.Path.LocalPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>();

        AddLocalSources(paths);
    }

    private async void ChooseInputFolderButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
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

        _inputFolderPath = folderPath;
        var searchOption = IncludeSubfoldersCheckBox.IsChecked == true
            ? SearchOption.AllDirectories
            : SearchOption.TopDirectoryOnly;

        try
        {
            var paths = Directory.EnumerateFiles(folderPath, "*.*", searchOption);
            AddLocalSources(paths);
        }
        catch (Exception ex)
        {
            ShowError($"無法讀取輸入資料夾：{ex.Message}");
        }
    }

    private async void ChooseOutputFolderButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
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
        OutputFolderText.Text = folderPath;
        StatusText.Text = "";
    }

    private async void AddUrlButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        await AddUrlSourceAsync();
    }

    private async void ImageUrlTextBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            await AddUrlSourceAsync();
        }
    }

    private async void ConvertButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_sources.Count == 0)
        {
            return;
        }

        await ConvertAllAsync();
    }

    private void ClearButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ClearSelection();
    }

    private void QualitySlider_PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (_isInitialized && e.Property == Slider.ValueProperty)
        {
            UpdateQualityText();
            StatusText.Text = "";
        }
    }

    private void OutputFormatComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_isInitialized)
        {
            return;
        }

        UpdateOutputFormatUi();
        StatusText.Text = "";
    }

    private void AddLocalSources(IEnumerable<string> paths)
    {
        var existing = new HashSet<string>(_sources.Select(source => source.Identity), StringComparer.OrdinalIgnoreCase);
        var added = 0;

        foreach (var path in paths)
        {
            if (!File.Exists(path) || !existing.Add(path))
            {
                continue;
            }

            try
            {
                _ = GetImageInfo(path);
                _sources.Add(SourceImage.FromLocalFile(path));
                added++;
            }
            catch
            {
                existing.Remove(path);
            }
        }

        RefreshSelectionState();

        if (added == 0 && _sources.Count == 0)
        {
            ShowError("沒有找到可支援的圖片檔案。若檔案格式仍無法辨識，請手動安裝 ImageMagick 後再試。");
        }
        else if (added == 0)
        {
            StatusText.Text = "沒有新增檔案，可能已在清單中。";
        }
        else
        {
            StatusText.Foreground = Brushes.ForestGreen;
            StatusText.Text = $"已新增 {added:N0} 個圖片檔案。";
        }

        LoadPreview();
    }

    private async Task AddUrlSourceAsync()
    {
        var url = ImageUrlTextBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            ShowError("請先輸入圖片網址或本機圖片路徑。");
            return;
        }

        if (File.Exists(url))
        {
            AddLocalImagePathFromTextBox(url);
            return;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            ShowError("請輸入有效的 http/https 圖片網址，或貼上已下載圖片的本機完整路徑。");
            return;
        }

        if (_sources.Any(source => source.Identity.Equals(uri.AbsoluteUri, StringComparison.OrdinalIgnoreCase)))
        {
            ShowError("這個網址已在清單中。");
            return;
        }

        SetBusy(true);
        StatusText.Foreground = Brushes.ForestGreen;
        StatusText.Text = "正在下載圖片...";

        try
        {
            var downloadedImage = await DownloadImageAsync(uri);
            var imageInfo = GetImageInfo(downloadedImage.TempPath);
            var displayName = GetDisplayNameFromUrl(downloadedImage.SourceUri, _sources.Count + 1);

            _temporaryFiles.Add(downloadedImage.TempPath);
            _sources.Add(SourceImage.FromUrl(downloadedImage.TempPath, uri.AbsoluteUri, displayName));

            ImageUrlTextBox.Text = "";
            RefreshSelectionState();
            LoadPreview();
            StatusText.Foreground = Brushes.ForestGreen;
            StatusText.Text = $"已加入網址圖片：{displayName}，{imageInfo.Width:N0} x {imageInfo.Height:N0} px";
        }
        catch (Exception ex)
        {
            ShowError($"無法加入網址圖片：{ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void AddLocalImagePathFromTextBox(string path)
    {
        try
        {
            _ = GetImageInfo(path);
            var existing = new HashSet<string>(_sources.Select(source => source.Identity), StringComparer.OrdinalIgnoreCase);
            if (!existing.Add(path))
            {
                ShowError("這個本機圖片已在清單中。");
                return;
            }

            _sources.Add(SourceImage.FromLocalFile(path));
            ImageUrlTextBox.Text = "";
            RefreshSelectionState();
            LoadPreview();
            StatusText.Foreground = Brushes.ForestGreen;
            StatusText.Text = $"已加入本機圖片：{Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            ShowError($"無法加入本機圖片：{ex.Message}");
        }
    }

    private static async Task<DownloadedImage> DownloadImageAsync(Uri uri)
    {
        var errors = new List<string>();

        foreach (var candidate in GetImageUriCandidates(uri))
        {
            try
            {
                var tempPath = await DownloadImageCandidateAsync(candidate);
                return new DownloadedImage(tempPath, candidate);
            }
            catch (Exception ex)
            {
                errors.Add($"{candidate}：{ex.Message}");
            }
        }

        throw new InvalidOperationException(errors.Count == 0
            ? "無法下載圖片。"
            : string.Join(Environment.NewLine, errors));
    }

    private static async Task<string> DownloadImageCandidateAsync(Uri uri)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        request.Headers.TryAddWithoutValidation("Accept", "image/avif,image/webp,image/jpeg,image/png,image/bmp,image/gif,image/*,*/*;q=0.8");
        request.Headers.TryAddWithoutValidation("Referer", GetRefererForImageHost(uri));

        using var response = await HttpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is not null && !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"網址沒有直接回傳圖片，Content-Type 是 {mediaType}。請複製圖片本身的直接連結，或先下載後用「選擇 PNG 檔案」加入。");
        }

        var tempDirectory = Path.Combine(Path.GetTempPath(), "PngToJpegConverter");
        Directory.CreateDirectory(tempDirectory);

        var extension = GetExtensionFromMediaType(mediaType) ?? Path.GetExtension(uri.AbsolutePath);
        if (string.IsNullOrWhiteSpace(extension) || extension.Length > 6)
        {
            extension = ".img";
        }

        var tempPath = Path.Combine(tempDirectory, $"{Guid.NewGuid():N}{extension}");
        await using (var source = await response.Content.ReadAsStreamAsync())
        await using (var target = File.Create(tempPath))
        {
            await source.CopyToAsync(target);
        }

        try
        {
            _ = GetImageInfo(tempPath);
        }
        catch
        {
            try
            {
                File.Delete(tempPath);
            }
            catch
            {
                // If cleanup fails, the file is in the temp folder and can be overwritten later.
            }

            throw new InvalidOperationException("下載完成，但內容無法解碼成圖片。這通常表示網址回傳的是 HTML 頁面、不是直接圖片連結，或格式需要手動安裝 ImageMagick 才能支援。");
        }

        return tempPath;
    }

    private static IEnumerable<Uri> GetImageUriCandidates(Uri uri)
    {
        foreach (var cleanedUri in GetCleanImageUris(uri))
        {
            if (!cleanedUri.AbsoluteUri.Equals(uri.AbsoluteUri, StringComparison.OrdinalIgnoreCase))
            {
                yield return cleanedUri;
            }
        }

        yield return uri;
    }

    private static IEnumerable<Uri> GetCleanImageUris(Uri uri)
    {
        var absolutePath = uri.GetLeftPart(UriPartial.Path);
        var extensions = new[] { ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif" };

        foreach (var extension in extensions)
        {
            var index = absolutePath.IndexOf(extension, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                continue;
            }

            var endIndex = index + extension.Length;
            if (endIndex < absolutePath.Length && absolutePath[endIndex] == '@')
            {
                yield return new Uri(absolutePath[..endIndex]);
            }
        }
    }

    private static string GetRefererForImageHost(Uri uri)
    {
        return uri.Host.EndsWith("hdslb.com", StringComparison.OrdinalIgnoreCase)
            ? "https://www.bilibili.com/"
            : $"{uri.Scheme}://{uri.Host}/";
    }

    private async Task ConvertAllAsync()
    {
        SetBusy(true);
        StatusText.Foreground = Brushes.ForestGreen;

        var outputFormat = GetSelectedOutputFormat();
        var quality = (int)Math.Round(QualitySlider.Value);
        var usedOutputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var completed = 0;
        var failed = 0;

        try
        {
            foreach (var source in _sources)
            {
                var outputPath = GetOutputPath(source, outputFormat, usedOutputs);
                StatusText.Text = $"正在轉換 {completed + failed + 1:N0} / {_sources.Count:N0}...";

                try
                {
                    await Task.Run(() => ConvertImage(source.FilePath, outputPath, outputFormat, quality));
                    completed++;
                }
                catch
                {
                    failed++;
                }
            }

            StatusText.Foreground = failed == 0 ? Brushes.ForestGreen : Brushes.DarkOrange;
            StatusText.Text = failed == 0
                ? $"完成：已轉換 {completed:N0} 個檔案。"
                : $"完成：成功 {completed:N0} 個，失敗 {failed:N0} 個。";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void LoadPreview()
    {
        var firstSource = _sources.FirstOrDefault();
        if (firstSource is null)
        {
            PreviewImage.Source = null;
            ImageInfoText.Text = "等待選取圖片";
            return;
        }

        try
        {
            var imageInfo = GetImageInfo(firstSource.FilePath);
            var previewPath = GetPreviewPath(firstSource.FilePath);
            using var previewStream = File.OpenRead(previewPath);
            PreviewImage.Source = new Bitmap(previewStream);
            ImageInfoText.Text = $"預覽：{firstSource.DisplayName}，{imageInfo.Width:N0} x {imageInfo.Height:N0} px";
        }
        catch (Exception ex)
        {
            PreviewImage.Source = null;
            ShowError(ex.Message);
        }
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
            // Fall through to Magick.NET for formats Skia cannot decode, such as AVIF.
        }

        try
        {
            using var image = new MagickImage(path);
            return new ImageSize((int)image.Width, (int)image.Height);
        }
        catch
        {
            throw new InvalidOperationException("這不是有效的圖片，或是不支援的圖片格式。若需要更完整格式支援，請手動安裝 ImageMagick 後再試。");
        }
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

            using var surface = SKSurface.Create(new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
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
            return;
        }
        catch
        {
            ConvertImageToJpegWithMagick(sourcePath, outputPath, quality);
        }
    }

    private static void ConvertImage(string sourcePath, string outputPath, OutputFormat outputFormat, int quality)
    {
        if (outputFormat == OutputFormat.Png)
        {
            ConvertImageToPng(sourcePath, outputPath);
            return;
        }

        ConvertImageToJpeg(sourcePath, outputPath, quality);
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
            return;
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
            throw new InvalidOperationException($"無法轉換此圖片格式。請手動安裝 ImageMagick 後再試。詳細資訊：{ex.Message}");
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
            throw new InvalidOperationException($"無法轉換此圖片格式。請手動安裝 ImageMagick 後再試。詳細資訊：{ex.Message}");
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

    private string GetOutputPath(SourceImage source, OutputFormat outputFormat, ISet<string> usedOutputs)
    {
        var directory = _outputFolderPath ??
            (source.IsUrlSource ? AppContext.BaseDirectory : Path.GetDirectoryName(source.FilePath)) ??
            AppContext.BaseDirectory;

        var fileName = SanitizeFileName(Path.GetFileNameWithoutExtension(source.OutputBaseName));
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = "converted-image";
        }

        var extension = GetOutputExtension(outputFormat);
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

    private void RefreshSelectionState()
    {
        SelectedFilesList.ItemsSource = _sources.Select(source => source.DisplayName).ToList();
        SourceSummaryText.Text = _sources.Count == 0
            ? "尚未選擇檔案、資料夾或網址"
            : $"已加入 {_sources.Count:N0} 張圖片";
        InputFolderText.Text = _inputFolderPath is null ? "" : $"輸入資料夾：{_inputFolderPath}";
        ConvertButton.IsEnabled = _sources.Count > 0;
    }

    private void ClearSelection()
    {
        _sources.Clear();
        _inputFolderPath = null;
        PreviewImage.Source = null;
        StatusText.Text = "";
        ImageUrlTextBox.Text = "";
        RefreshSelectionState();
        ImageInfoText.Text = "等待選取圖片";
        DeleteTemporaryFiles();
    }

    private void SetBusy(bool isBusy)
    {
        ChooseFilesButton.IsEnabled = !isBusy;
        ChooseInputFolderButton.IsEnabled = !isBusy;
        ChooseOutputFolderButton.IsEnabled = !isBusy;
        OutputFormatComboBox.IsEnabled = !isBusy;
        AddUrlButton.IsEnabled = !isBusy;
        ImageUrlTextBox.IsEnabled = !isBusy;
        ClearButton.IsEnabled = !isBusy;
        ConvertButton.IsEnabled = !isBusy && _sources.Count > 0;
    }

    private void ShowError(string message)
    {
        StatusText.Foreground = Brushes.Firebrick;
        StatusText.Text = message;
    }

    private void UpdateQualityText()
    {
        QualityText.Text = GetSelectedOutputFormat() == OutputFormat.Jpeg
            ? $"{(int)Math.Round(QualitySlider.Value)}%"
            : "PNG";
    }

    private void UpdateOutputFormatUi()
    {
        var outputFormat = GetSelectedOutputFormat();
        var isJpeg = outputFormat == OutputFormat.Jpeg;

        QualityTitleText.Text = isJpeg ? "JPEG 品質" : "PNG 輸出";
        QualitySlider.IsEnabled = isJpeg;
        ConvertButton.Content = $"全部轉換成 {GetOutputFormatDisplayName(outputFormat)}";
        UpdateQualityText();
    }

    private OutputFormat GetSelectedOutputFormat()
    {
        return OutputFormatComboBox.SelectedIndex == 1 ? OutputFormat.Png : OutputFormat.Jpeg;
    }

    private static string GetOutputExtension(OutputFormat outputFormat)
    {
        return outputFormat == OutputFormat.Png ? ".png" : ".jpg";
    }

    private static string GetOutputFormatDisplayName(OutputFormat outputFormat)
    {
        return outputFormat == OutputFormat.Png ? "PNG" : "JPEG";
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
                // Best effort cleanup for downloaded preview files.
            }
        }

        _temporaryFiles.Clear();
    }

    private static string GetDisplayNameFromUrl(Uri uri, int index)
    {
        var fileName = Path.GetFileName(uri.LocalPath);
        var modifierIndex = fileName.IndexOf('@');
        if (modifierIndex > 0)
        {
            fileName = fileName[..modifierIndex];
        }

        return string.IsNullOrWhiteSpace(fileName)
            ? $"url-image-{index}.png"
            : fileName;
    }

    private static string? GetExtensionFromMediaType(string? mediaType)
    {
        return mediaType?.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/jpg" => ".jpg",
            "image/avif" => ".avif",
            "image/heic" => ".heic",
            "image/heif" => ".heif",
            "image/tiff" => ".tif",
            "image/webp" => ".webp",
            "image/gif" => ".gif",
            "image/bmp" => ".bmp",
            _ => null
        };
    }

    private static string SanitizeFileName(string fileName)
    {
        foreach (var character in Path.GetInvalidFileNameChars())
        {
            fileName = fileName.Replace(character, '_');
        }

        return fileName.Trim();
    }

    private string GetPreviewPath(string sourcePath)
    {
        try
        {
            using var stream = File.OpenRead(sourcePath);
            using var _ = new Bitmap(stream);
            return sourcePath;
        }
        catch
        {
            var tempDirectory = Path.Combine(Path.GetTempPath(), "PngToJpegConverter");
            Directory.CreateDirectory(tempDirectory);
            var previewPath = Path.Combine(tempDirectory, $"{Guid.NewGuid():N}-preview.jpg");
            ConvertImageToJpegWithMagick(sourcePath, previewPath, 95);
            _temporaryFiles.Add(previewPath);
            return previewPath;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        DeleteTemporaryFiles();
        base.OnClosed(e);
    }

    private sealed record SourceImage(
        string FilePath,
        string Identity,
        string DisplayName,
        string OutputBaseName,
        bool IsUrlSource)
    {
        public static SourceImage FromLocalFile(string path)
        {
            var fileName = Path.GetFileName(path);
            return new SourceImage(path, path, fileName, fileName, false);
        }

        public static SourceImage FromUrl(string tempPath, string url, string displayName)
        {
            return new SourceImage(tempPath, url, $"網址：{displayName}", displayName, true);
        }
    }

    private readonly record struct ImageSize(int Width, int Height);

    private readonly record struct DownloadedImage(string TempPath, Uri SourceUri);

    private enum OutputFormat
    {
        Jpeg,
        Png
    }
}
