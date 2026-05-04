# avaloniaPNGtoJPEGconverter

一款基於 Avalonia 的桌面小程式，可選取 PNG 圖片並轉換成 JPEG。

## 功能

- 選取單一 PNG 檔案
- 預覽來源圖片
- 顯示圖片尺寸與預設輸出路徑
- 調整 JPEG 品質 1% 到 100%
- 轉檔至原資料夾，或使用「另存為」選擇輸出位置
- PNG 透明區域會以白底合成後輸出，避免 JPEG 透明度遺失造成黑底

## 執行

```powershell
dotnet run
```

## 建置

```powershell
dotnet build
```
