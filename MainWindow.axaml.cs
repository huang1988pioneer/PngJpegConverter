using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using SkiaSharp;

namespace PngToJpegConverter;

public partial class MainWindow : Window
{
    private static readonly FilePickerFileType PngFileType = new("PNG 圖片")
    {
        Patterns = new[] { "*.png" },
        AppleUniformTypeIdentifiers = new[] { "public.png" },
        MimeTypes = new[] { "image/png" }
    };

    private string? _sourcePath;
    private string? _defaultOutputPath;

    public MainWindow()
    {
        InitializeComponent();
        UpdateQualityText();
    }

    private async void ChooseButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "選擇 PNG 圖片",
            AllowMultiple = false,
            FileTypeFilter = new[] { PngFileType }
        });

        var file = files.FirstOrDefault();
        if (file?.Path.LocalPath is not { Length: > 0 } path)
        {
            return;
        }

        await LoadSourceAsync(path);
    }

    private async void ConvertButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_sourcePath is null || _defaultOutputPath is null)
        {
            return;
        }

        await ConvertAsync(_sourcePath, _defaultOutputPath);
    }

    private async void SaveAsButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_sourcePath is null)
        {
            return;
        }

        var suggestedName = Path.GetFileName(_defaultOutputPath ?? "converted.jpg");
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "儲存 JPEG",
            SuggestedFileName = suggestedName,
            DefaultExtension = "jpg",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("JPEG 圖片")
                {
                    Patterns = new[] { "*.jpg", "*.jpeg" },
                    MimeTypes = new[] { "image/jpeg" }
                }
            }
        });

        if (file?.Path.LocalPath is { Length: > 0 } outputPath)
        {
            await ConvertAsync(_sourcePath, outputPath);
        }
    }

    private void QualitySlider_PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Slider.ValueProperty)
        {
            UpdateQualityText();
            StatusText.Text = "";
        }
    }

    private async Task LoadSourceAsync(string path)
    {
        SetBusy(true);
        StatusText.Foreground = Avalonia.Media.Brushes.ForestGreen;
        StatusText.Text = "";

        try
        {
            await using var previewStream = File.OpenRead(path);
            PreviewImage.Source = new Bitmap(previewStream);

            using var codec = SKCodec.Create(path);
            if (codec is null || codec.EncodedFormat != SKEncodedImageFormat.Png)
            {
                throw new InvalidOperationException("這個檔案不是有效的 PNG 圖片。");
            }

            _sourcePath = path;
            _defaultOutputPath = GetDefaultOutputPath(path);

            SourcePathText.Text = path;
            ImageInfoText.Text = $"{codec.Info.Width:N0} x {codec.Info.Height:N0} px";
            OutputPathText.Text = _defaultOutputPath;
            ConvertButton.IsEnabled = true;
            SaveAsButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            ClearSelection();
            ShowError(ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ConvertAsync(string sourcePath, string outputPath)
    {
        SetBusy(true);
        StatusText.Text = "正在轉換...";
        StatusText.Foreground = Avalonia.Media.Brushes.ForestGreen;

        try
        {
            var quality = (int)Math.Round(QualitySlider.Value);
            await Task.Run(() => ConvertPngToJpeg(sourcePath, outputPath, quality));
            StatusText.Text = $"完成：{outputPath}";
            OutputPathText.Text = outputPath;
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static void ConvertPngToJpeg(string sourcePath, string outputPath, int quality)
    {
        using var input = File.OpenRead(sourcePath);
        using var bitmap = SKBitmap.Decode(input);
        if (bitmap is null)
        {
            throw new InvalidOperationException("無法讀取 PNG 圖片。");
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

        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var output = File.Open(outputPath, FileMode.Create, FileAccess.Write);
        data.SaveTo(output);
    }

    private static string GetDefaultOutputPath(string sourcePath)
    {
        var directory = Path.GetDirectoryName(sourcePath) ?? "";
        var fileName = Path.GetFileNameWithoutExtension(sourcePath);
        var candidate = Path.Combine(directory, $"{fileName}.jpg");

        if (!File.Exists(candidate))
        {
            return candidate;
        }

        var index = 1;
        string numbered;
        do
        {
            numbered = Path.Combine(directory, $"{fileName}-{index}.jpg");
            index++;
        }
        while (File.Exists(numbered));

        return numbered;
    }

    private void ClearSelection()
    {
        _sourcePath = null;
        _defaultOutputPath = null;
        PreviewImage.Source = null;
        SourcePathText.Text = "尚未選擇檔案";
        ImageInfoText.Text = "等待選取圖片";
        OutputPathText.Text = "";
        ConvertButton.IsEnabled = false;
        SaveAsButton.IsEnabled = false;
    }

    private void SetBusy(bool isBusy)
    {
        ChooseButton.IsEnabled = !isBusy;
        ConvertButton.IsEnabled = !isBusy && _sourcePath is not null;
        SaveAsButton.IsEnabled = !isBusy && _sourcePath is not null;
    }

    private void ShowError(string message)
    {
        StatusText.Foreground = Avalonia.Media.Brushes.Firebrick;
        StatusText.Text = message;
    }

    private void UpdateQualityText()
    {
        QualityText.Text = $"{(int)Math.Round(QualitySlider.Value)}%";
    }
}
