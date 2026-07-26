# 圖片格式轉換器

依設計稿實作的 Avalonia 桌面應用：支援 **PNG ↔ JPEG 互轉** 與 **批量轉換**。

## 介面（對齊設計稿）

- 頂部標題列：應用圖示、名稱、版本與副標
- 左側導覽：格式轉換 / 歷史記錄 / 設定 / 關於我們
- 側邊拖放區：拖入圖片或資料夾
- 步驟 1：選擇轉換方式（PNG → JPEG / JPEG → PNG）
- 步驟 2：檔案清單（縮圖、檔名、格式、大小、狀態、單筆移除）
- 步驟 3：輸出格式、JPEG 品質、輸出資料夾
- 底部：轉換進度、開始轉換、取消轉換

## 功能

- 多選檔案、資料夾掃描（可含子資料夾）
- 拖放檔案 / 資料夾加入清單
- PNG ↔ JPEG 雙向轉換（亦可讀取 WebP、AVIF、BMP、GIF、TIFF、HEIC 等常見格式）
- JPEG 品質 1%–100%（預設 90%）
- 批量轉換與即時進度、可中途取消
- 輸出資料夾可自訂；未指定時輸出到原圖資料夾
- 同名檔自動加流水號，避免覆蓋
- JPEG 輸出時透明區域以白底合成
- 轉換歷史記錄
- 設定：預設品質、含子資料夾、完成後開啟資料夾

## 執行

```powershell
dotnet run
```

## 建置

```powershell
dotnet build -c Release
```

執行檔位置：

```
bin\Release\net8.0\win-x64\PngToJpegConverter.exe
```

（若未指定 RuntimeIdentifier，則在 `bin\Release\net8.0\`）

## 技術

- Avalonia UI · .NET 8
- SkiaSharp · Magick.NET
