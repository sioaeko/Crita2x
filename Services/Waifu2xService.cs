using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Waifu16K.Services;

public sealed record Waifu2xOptions(
    string EnginePath,
    string Model,
    int Scale,
    int Noise,
    int TileSize,
    string GpuId,
    int LoadThreads,
    int ProcThreads,
    int SaveThreads,
    bool Tta,
    string Format,
    string OutputSuffix);

public sealed record Waifu2xModelOption(string DisplayName, string ModelPath);

public static class Waifu2xService
{
    private static readonly Regex PercentRegex = new(@"(?<value>\d{1,3})\s*%", RegexOptions.Compiled);

    public static string FindDefaultEngine()
    {
        string baseDirectory = AppContext.BaseDirectory;
        string[] candidates =
        [
            Path.Combine(baseDirectory, "engines", "waifu2x-ncnn-vulkan", "waifu2x-ncnn-vulkan.exe"),
            Path.Combine(Environment.CurrentDirectory, "engines", "waifu2x-ncnn-vulkan", "waifu2x-ncnn-vulkan.exe"),
            Path.Combine(Environment.CurrentDirectory, "waifu2x-ncnn-vulkan.exe")
        ];

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        foreach (string directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(directory.Trim(), "waifu2x-ncnn-vulkan.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return string.Empty;
    }

    public static IReadOnlyList<Waifu2xModelOption> DiscoverModels(string enginePath)
    {
        string? engineDirectory = File.Exists(enginePath)
            ? Path.GetDirectoryName(enginePath)
            : null;
        var models = new List<Waifu2xModelOption>();

        if (!string.IsNullOrWhiteSpace(engineDirectory) && Directory.Exists(engineDirectory))
        {
            foreach (string knownModel in KnownModelNames())
            {
                string directory = Path.Combine(engineDirectory, knownModel);
                if (IsModelDirectory(directory))
                {
                    models.Add(CreateModelOption(directory, engineDirectory));
                }
            }

            foreach (string directory in Directory.EnumerateDirectories(engineDirectory)
                .Where(IsModelDirectory)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                string name = Path.GetFileName(directory);
                if (models.Any(model => string.Equals(model.ModelPath, name, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                models.Add(CreateModelOption(directory, engineDirectory));
            }
        }

        return models.Count > 0
            ? models
            : KnownModelNames().Select(name => new Waifu2xModelOption(GetModelDisplayName(name), name)).ToArray();
    }

    public static bool IsModelDirectory(string path)
    {
        return Directory.Exists(path)
            && Directory.EnumerateFiles(path, "*.param", SearchOption.TopDirectoryOnly).Any()
            && Directory.EnumerateFiles(path, "*.bin", SearchOption.TopDirectoryOnly).Any();
    }

    public static async Task<string> RunAsync(
        string inputPath,
        string outputDirectory,
        Waifu2xOptions options,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(options.EnginePath))
        {
            throw new FileNotFoundException("waifu2x-ncnn-vulkan.exe를 찾을 수 없습니다.", options.EnginePath);
        }

        Directory.CreateDirectory(outputDirectory);

        string format = ResolveFormat(inputPath, options.Format);
        string extension = format.Equals("jpg", StringComparison.OrdinalIgnoreCase) ? ".jpg" : $".{format}";
        string outputPath = CreateOutputPath(inputPath, outputDirectory, options.Scale, options.Noise, extension, options.OutputSuffix);
        string modelDirectory = Path.Combine(Path.GetDirectoryName(options.EnginePath) ?? string.Empty, options.Model);

        var arguments = new StringBuilder();
        AppendArgument(arguments, "-i", inputPath);
        AppendArgument(arguments, "-o", outputPath);
        AppendArgument(arguments, "-s", options.Scale.ToString());
        AppendArgument(arguments, "-n", options.Noise.ToString());
        AppendArgument(arguments, "-t", options.TileSize.ToString());
        AppendArgument(arguments, "-j", $"{options.LoadThreads}:{options.ProcThreads}:{options.SaveThreads}");
        if (!options.GpuId.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            AppendArgument(arguments, "-g", options.GpuId);
        }
        AppendArgument(arguments, "-f", format);

        if (Directory.Exists(modelDirectory))
        {
            AppendArgument(arguments, "-m", modelDirectory);
        }
        else
        {
            AppendArgument(arguments, "-m", options.Model);
        }

        if (options.Tta)
        {
            arguments.Append(" -x");
        }

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = options.EnginePath,
            Arguments = arguments.ToString(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            WorkingDirectory = Path.GetDirectoryName(options.EnginePath) ?? Environment.CurrentDirectory
        };

        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration registration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Process may already be gone.
            }
        });

        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => completion.TrySetResult(process.ExitCode);

        DataReceivedEventHandler outputHandler = (_, eventArgs) =>
        {
            if (eventArgs.Data is null)
            {
                return;
            }

            Match match = PercentRegex.Match(eventArgs.Data);
            if (match.Success && int.TryParse(match.Groups["value"].Value, out int value))
            {
                progress?.Report(Math.Clamp(value, 0, 99));
            }
        };

        process.OutputDataReceived += outputHandler;
        process.ErrorDataReceived += outputHandler;

        progress?.Report(1);

        if (!process.Start())
        {
            throw new InvalidOperationException("Waifu2x 엔진을 시작하지 못했습니다.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        int exitCode = await completion.Task.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        if (exitCode != 0)
        {
            throw new InvalidOperationException($"Waifu2x 엔진이 실패했습니다. 종료 코드: {exitCode}");
        }

        if (!File.Exists(outputPath))
        {
            throw new FileNotFoundException("엔진이 출력 파일을 만들지 않았습니다.", outputPath);
        }

        progress?.Report(100);
        return outputPath;
    }

    private static string[] KnownModelNames()
    {
        return
        [
            "models-cunet",
            "models-upconv_7_anime_style_art_rgb",
            "models-upconv_7_photo"
        ];
    }

    private static Waifu2xModelOption CreateModelOption(string directory, string engineDirectory)
    {
        string name = Path.GetFileName(directory);
        string modelPath = string.Equals(Path.GetDirectoryName(directory), engineDirectory, StringComparison.OrdinalIgnoreCase)
            ? name
            : directory;
        return new Waifu2xModelOption(GetModelDisplayName(name), modelPath);
    }

    private static string GetModelDisplayName(string modelName)
    {
        return modelName switch
        {
            "models-cunet" => "CUNet · 일러스트/범용 고품질",
            "models-upconv_7_anime_style_art_rgb" => "UpConv Anime · 애니/선화 빠른 처리",
            "models-upconv_7_photo" => "UpConv Photo · 사진/실사",
            _ => $"Custom · {modelName}"
        };
    }

    private static string CreateOutputPath(string inputPath, string outputDirectory, int scale, int noise, string extension, string outputSuffix)
    {
        string name = Path.GetFileNameWithoutExtension(inputPath);
        string suffix = SanitizeSuffix(outputSuffix, scale, noise);
        if (string.IsNullOrWhiteSpace(suffix))
        {
            suffix = $"_crita2x_{scale}x_n{noise}";
        }

        string outputPath = Path.Combine(outputDirectory, $"{name}{suffix}{extension}");
        int index = 2;

        while (File.Exists(outputPath))
        {
            outputPath = Path.Combine(outputDirectory, $"{name}{suffix}_{index}{extension}");
            index++;
        }

        return outputPath;
    }

    private static string ResolveFormat(string inputPath, string requestedFormat)
    {
        if (!requestedFormat.Equals("original", StringComparison.OrdinalIgnoreCase))
        {
            return requestedFormat.Equals("jpeg", StringComparison.OrdinalIgnoreCase)
                ? "jpg"
                : requestedFormat.ToLowerInvariant();
        }

        string extension = Path.GetExtension(inputPath).TrimStart('.').ToLowerInvariant();
        return extension switch
        {
            "jpg" or "jpeg" => "jpg",
            "webp" => "webp",
            _ => "png"
        };
    }

    private static string SanitizeSuffix(string suffix, int scale, int noise)
    {
        if (string.IsNullOrWhiteSpace(suffix))
        {
            return string.Empty;
        }

        suffix = suffix
            .Replace("{scale}", scale.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{noise}", noise.ToString(), StringComparison.OrdinalIgnoreCase);

        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            suffix = suffix.Replace(invalid, '_');
        }

        return suffix.Trim();
    }

    private static void AppendArgument(StringBuilder builder, string name, string value)
    {
        builder.Append(' ');
        builder.Append(name);
        builder.Append(' ');
        builder.Append('"');
        builder.Append(value.Replace("\"", "\\\"", StringComparison.Ordinal));
        builder.Append('"');
    }
}
