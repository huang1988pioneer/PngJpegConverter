# avaloniaPNGtoJPEGconverter

一款基於 Avalonia 的桌面小程式，可批次選取 PNG 圖片、輸入圖片網址，並轉換成 JPEG。

## 功能

- 可一次多選任意數量的 PNG 檔案
- 可選擇輸入資料夾，自動加入資料夾內的 PNG
- 可選擇是否包含子資料夾
- 可輸入圖片網址並加入轉換清單
- 可顯示來源圖片預覽
- 可選擇輸出資料夾；未選擇時，本機檔案輸出到原資料夾，網址圖片輸出到程式資料夾
- 調整 JPEG 品質 1% 到 100%
- PNG 透明區域會以白底合成後輸出，避免 JPEG 透明度遺失造成黑底
- 同名輸出檔會自動加上流水號，避免覆蓋既有檔案

## 執行

```powershell
dotnet run
```

## 建置

```powershell
dotnet build
```
