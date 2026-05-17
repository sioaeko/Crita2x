# Crita2x

Crita2x is a native Windows WPF workspace for image upscaling, cutout cleanup, and quick finishing around `waifu2x-ncnn-vulkan`.

![Crita2x screenshot](docs/assets/crita2x.png)

## Highlights

- Batch image queue with drag and drop
- `waifu2x-ncnn-vulkan` runner with GPU selection, tile size, TTA, output format, and thread controls
- Dynamic model discovery from the selected engine folder
- Custom model folder support
- Background removal tools: border cleanup, chroma key, color picker, feathering, defringe, transparent trim
- Brush tools: alpha erase, alpha restore, local mask-based auto restore
- Quick editor: rotate, flip, crop, resize, tone, denoise, sharpen, auto enhance
- Native dark UI with custom in-app window controls

## Requirements

- Windows 10 or later
- .NET 8 Desktop Runtime or .NET 8 SDK
- Vulkan-capable GPU recommended
- `waifu2x-ncnn-vulkan` Windows release

## Engine Setup

This repository does not bundle the waifu2x engine or model binaries.

Download `waifu2x-ncnn-vulkan`, then place it here:

```text
engines/
  waifu2x-ncnn-vulkan/
    waifu2x-ncnn-vulkan.exe
    models-cunet/
    models-upconv_7_anime_style_art_rgb/
    models-upconv_7_photo/
```

The app also lets you choose `waifu2x-ncnn-vulkan.exe` from the engine field in the title bar. Model folders next to the selected executable are scanned automatically.

## Build

```powershell
dotnet build
dotnet run
```

If the SDK is not on `PATH`, run it with the full path:

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' build
```

## Portable Publish

For a self-contained Windows build:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o publish/Crita2x
```

Copy the `engines/waifu2x-ncnn-vulkan` folder into the published folder if you want the app to find the engine automatically.

## Project Status

Crita2x is an active work-in-progress. The core upscaling, queue, preview, cutout, and editing flows are implemented, but packaging and end-to-end QA are still being refined.
