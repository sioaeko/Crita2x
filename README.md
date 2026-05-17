# Crita2x

Native Windows workspace for waifu2x upscaling, cutout cleanup, and quick image editing.

![Windows](https://img.shields.io/badge/platform-Windows-0078D4?style=flat-square)
![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4?style=flat-square)
![WPF](https://img.shields.io/badge/UI-WPF-2D2D30?style=flat-square)
![Status](https://img.shields.io/badge/status-work%20in%20progress-f0b35a?style=flat-square)

![Crita2x screenshot](docs/assets/crita2x.png)

Crita2x is a desktop app built around [`waifu2x-ncnn-vulkan`](https://github.com/nihui/waifu2x-ncnn-vulkan). It combines batch upscaling, preview navigation, background removal, alpha cleanup, and small finishing edits in one native WPF interface.

## Features

**Upscaling**

- Batch queue with drag and drop
- Scale, denoise, tile size, GPU, TTA, format, and thread controls
- Dynamic model discovery from the selected engine folder
- External custom model folder support
- Output folder and naming controls

**Cutout**

- Border-based background removal
- Chroma key with color picking
- Feathering, defringe, and transparent trim
- Alpha eraser and alpha restore brushes
- Local mask-based auto restore brush for recovering cutout edges

**Editing**

- Rotate, flip, crop, and long-side resize
- Brightness, contrast, saturation, denoise, sharpen, and auto enhance
- Undo and redo
- PNG, JPEG, TIFF, and BMP export

## Requirements

- Windows 10 or later
- .NET 8 Desktop Runtime or .NET 8 SDK
- Vulkan-capable GPU recommended
- `waifu2x-ncnn-vulkan` for Windows

## Engine Setup

This repository does not include waifu2x engine or model binaries.

Download a Windows release of [`waifu2x-ncnn-vulkan`](https://github.com/nihui/waifu2x-ncnn-vulkan/releases), then place it under:

```text
engines/
  waifu2x-ncnn-vulkan/
    waifu2x-ncnn-vulkan.exe
    models-cunet/
    models-upconv_7_anime_style_art_rgb/
    models-upconv_7_photo/
```

You can also select `waifu2x-ncnn-vulkan.exe` from the engine field inside the app. Model folders next to the selected executable are detected automatically.

## Build From Source

```powershell
dotnet build
dotnet run
```

If `dotnet` is not on `PATH`, use the full SDK path:

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' build
```

## Portable Publish

Create a self-contained Windows build:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o publish/Crita2x
```

To make the published app detect the engine automatically, copy `engines/waifu2x-ncnn-vulkan` into the published folder.

## Project Status

Crita2x is under active development. Core upscaling, queue management, preview navigation, cutout tools, and editing tools are implemented. Packaging, installer support, and broader QA are still in progress.

## Repository Notes

- Engine binaries, downloaded archives, build output, and test artifacts are intentionally ignored.
- The project file is still named `Waifu16K.csproj`; the app assembly and user-facing name are `Crita2x`.
