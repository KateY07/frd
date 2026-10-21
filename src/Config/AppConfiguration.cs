using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Frd;

public sealed record CodecPreset
{
    public string Label { get; set; } = "";
    public string EncoderArguments { get; set; } = "";
    public string DecoderArguments { get; set; } = "";
    public string InputPixelMode { get; set; } = "bgra";
    public bool BitrateControlled { get; set; } = true;
    public bool AutoProbe { get; set; }
    public int MinimumAutoBitrateKbps { get; set; } = 100;
    public bool Enabled { get; set; } = true;
    public string? UnavailableReason { get; set; }
}

public sealed record AppConfiguration
{
    public int SchemaVersion { get; set; } = 3;
    public int Width { get; set; } = 1280;
    public int Height { get; set; } = 720;
    public double TransmissionScale { get; set; } = 1;
    public int FramesPerSecond { get; set; } = 30;
    public string InitialPreset { get; set; } = "h264";
    public bool AutoSelectCodec { get; set; }
    public int InitialBitrateKbps { get; set; } = 2500;
    public double MaximumBitrateMbps { get; set; } = 100;
    [System.Text.Json.Serialization.JsonIgnore]
    public int MaximumBitrateKbps => checked((int)Math.Round(MaximumBitrateMbps * 1000));
    public string LibraryDirectory { get; set; } = "ffmpeg";
    public NetworkSimulation Simulation { get; set; } = new();
    public Dictionary<string, CodecPreset> Presets { get; set; } = new();

    public static AppConfiguration Load(string path)
    {
        var config = JsonSerializer.Deserialize<AppConfiguration>(File.ReadAllText(path), JsonOptions) ?? throw new InvalidDataException("Empty codec configuration.");
        if (config.SchemaVersion != 3 || config.Width < 64 || config.Height < 64 || config.Width > 7680 || config.Height > 4320 || config.Width % 2 != 0 || config.Height % 2 != 0)
            throw new InvalidDataException("Expected schemaVersion=3 and even video dimensions between 64 and 7680×4320.");
        if (!double.IsFinite(config.MaximumBitrateMbps) || config.MaximumBitrateMbps is < .1 or > 1000)
            throw new InvalidDataException("maximumBitrateMbps must be between 0.1 and 1000.");
        if (config.FramesPerSecond is not (10 or 20 or 25 or 30) || config.InitialBitrateKbps < 100 || config.InitialBitrateKbps > config.MaximumBitrateKbps || !config.Presets.ContainsKey(config.InitialPreset))
            throw new InvalidDataException("Invalid FPS, initial bitrate, or initial preset.");
        TransmissionGeometry.ValidateScale(config.TransmissionScale);
        foreach (var (id, preset) in config.Presets.Where(entry => entry.Value.Enabled))
        {
            var pixelMode = CapturePixelModes.Parse(preset.InputPixelMode);
            if (!preset.BitrateControlled || preset.MinimumAutoBitrateKbps < 100 ||
                preset.EncoderArguments.Contains("frd_lz4", StringComparison.OrdinalIgnoreCase) ||
                preset.DecoderArguments.Contains("frd_lz4", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Preset {id}: only bitrate-controlled FFmpeg codecs are supported.");
        }
        var directory = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, config.LibraryDirectory));
        FfmpegRuntime.Configure(directory);
        return config.WithAvailability();
    }

    /// <summary>Marks presets whose codec is not installed (FFmpeg extension missing, no Media Foundation H.264) as disabled.</summary>
    public AppConfiguration WithAvailability()
    {
        var mfEncoder = MediaFoundationRuntime.HasH264Encoder();
        var mfDecoder = MediaFoundationRuntime.HasH264Decoder();
        var presets = Presets.ToDictionary(entry => entry.Key, entry =>
        {
            var preset = entry.Value;
            if (!preset.Enabled) return preset;
            if (MfArguments.IsMediaFoundation(preset.EncoderArguments))
            {
                if (!mfEncoder) return preset with { Enabled = false, UnavailableReason = "此系统缺少 Media Foundation H.264 编码器（N 版 Windows 需安装媒体功能包）" };
                if (!mfDecoder) return preset with { Enabled = false, UnavailableReason = "此系统缺少 Media Foundation H.264 解码器" };
                return preset;
            }
            return FfmpegRuntime.IsAvailable ? preset : preset with { Enabled = false, UnavailableReason = "需要 FFmpeg 扩展包（把其中的 ffmpeg 文件夹解压到 FRD.exe 旁）" };
        });
        return this with { Presets = presets };
    }

    /// <summary>Falls back to the first installed codec when the configured initial preset is unavailable; null when none is.</summary>
    public AppConfiguration? WithUsableInitialPreset(out string? problem)
    {
        problem = null;
        if (Presets.TryGetValue(InitialPreset, out var initial) && initial.Enabled) return this;
        var fallback = Presets.FirstOrDefault(entry => entry.Value.Enabled && entry.Value.BitrateControlled);
        if (fallback.Key != null)
        {
            Console.Error.WriteLine($"Initial preset \"{InitialPreset}\" is unavailable ({Presets.GetValueOrDefault(InitialPreset)?.UnavailableReason ?? "not defined"}); using \"{fallback.Key}\".");
            return this with { InitialPreset = fallback.Key };
        }
        problem = "没有可用的 H.264 编解码器：请在 Windows 中安装“媒体功能包”（N 版/KN 版），或安装 FRD 的 FFmpeg 扩展包。";
        return null;
    }

    public static readonly JsonSerializerOptions JsonOptions = FrdJson.Config;
}
