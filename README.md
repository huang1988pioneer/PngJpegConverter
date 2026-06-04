# avaloniaPNGtoJPEGconverter

一款基於 Avalonia 的桌面小程式，可批次選取圖片、輸入圖片網址，並轉換成 JPEG 或 PNG。

## 功能

- 可一次多選任意數量的圖片檔案
- 可選擇輸入資料夾，自動掃描可支援的圖片
- 可選擇是否包含子資料夾
- 可輸入圖片網址或本機圖片路徑並加入轉換清單
- 可顯示來源圖片預覽
- 可選擇輸出資料夾；未選擇時，本機檔案輸出到原資料夾，網址圖片輸出到程式資料夾
- 支援 PNG、JPEG、WebP、AVIF、BMP、GIF、TIFF、HEIC/HEIF 等常見格式轉 JPEG 或 PNG
- 可選擇輸出格式為 JPEG 或 PNG
- 調整 JPEG 品質 1% 到 100%，預設 100%
- 輸出 JPEG 時，透明區域會以白底合成後輸出，避免透明度遺失造成黑底
- 同名輸出檔會自動加上流水號，避免覆蓋既有檔案

## ImageMagick

程式已內建 Magick.NET 以支援更多圖片格式。若遇到仍無法解碼的特殊格式，請手動安裝 ImageMagick：

https://imagemagick.org/script/download.php#windows

安裝後重新開啟程式再試。

## 執行

```powershell
dotnet run
```

## 建置

```powershell
dotnet build
```
