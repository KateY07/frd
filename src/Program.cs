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

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        CrashLog.Install();
        try
        {
            if (args is ["--help"] or ["--help-window"]) { RemoteLaunch.Help(args[0] == "--help-window"); return 0; }
            if (args is ["--version"])
            {
                Console.WriteLine(System.Reflection.Assembly.GetExecutingAssembly().GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                    .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().Single().InformationalVersion);
                return 0;
            }
            if (args.Length > 0 && args[0] == "--mf-regression") return MfRegression.Run(args);
            if (args is ["--auto-codec-probe", _, _] or ["--auto-decode-probe", _, _, _])
                return AutoCodecProbe.RunWorker(args);
            if (args.Length > 0 && args[0] is not ("--host" or "--connect" or "--codec-regression" or "--control-regression" or
                "--presentation-regression" or "--static-regression" or "--demo-regression" or "--cpu-presets-regression" or
                "--auto-probe-regression" or "--gpu-prereadback-regression" or "--gpu-quantized-encode-regression" or "--packed-presets-regression")) throw new ArgumentException(RemoteLaunch.Usage);
            var remoteOptions = args.Length > 0 && args[0] is "--host" or "--connect" ? RemoteLaunch.Parse(args) : null;
            var configPath = Path.Combine(AppContext.BaseDirectory, "codec-config.json");
            var config = AppConfiguration.Load(configPath);
            if (args.Length == 0 || args[0] is "--host" or "--connect")
            {
                var usable = config.WithUsableInitialPreset(out var codecProblem);
                if (usable == null) { Console.Error.WriteLine(codecProblem); return 2; }
                config = usable;
            }
            if (args.Length > 0 && args[0] == "--gpu-prereadback-regression")
                return GpuPreReadbackRegression.Run(args.Length > 1 ? args[1] : "results/gpu-prereadback.json");
            if (args.Length > 0 && args[0] == "--gpu-quantized-encode-regression")
                return GpuQuantizedEncodeRegression.Run(args.Length > 1 ? args[1] : "results/gpu-quantized-encode.json", args.Length > 2 ? args[2] : null);
            if (args.Length > 0 && args[0] == "--packed-presets-regression")
            {
                GpuQuantizedEncodeRegression.RunSessionAsync(config, args.Length > 1 ? args[1] : "results/packed-presets.json")
                    .GetAwaiter().GetResult();
                return 0;
            }
            if (args.Length > 0 && args[0] == "--auto-probe-regression")
            {
                var selected = AutoCodecProbe.SelectSender(config);
                var receiver = AutoCodecProbe.SelectReceiver(config, selected.Samples);
                var output = args.Length > 1 ? args[1] : "results/auto-codec-probe.json";
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
                File.WriteAllText(output, ReportJson.Serialize(new
                {
                    SelectedPreset = selected.Configuration.InitialPreset,
                    ReceiverSelectedPreset = receiver.InitialPreset,
                    Candidates = selected.Samples.Select(sample => new
                    { sample.PresetId, sample.CaptureMs, sample.EncodeMs, sample.LocalDecodeMs, Bytes = sample.Packet.Length })
                }, AppConfiguration.JsonOptions));
                return selected.Samples.Length > 0 ? 0 : 1;
            }
            if (remoteOptions != null)
            {
                var selected = remoteOptions.Host ? AutoCodecProbe.SelectSender(config) : (config, Array.Empty<CodecProbeSample>());
                return RemoteLaunch.Run(selected.Item1, remoteOptions, selected.Item2);
            }
            if (args.Length > 0 && args[0] == "--codec-regression") { FfmpegRegression.Run(config, args.Length > 1 ? args[1] : "results/ffmpeg-regression"); return Environment.ExitCode; }
            if (args.Length > 0 && args[0] == "--control-regression") { FfmpegRegression.RunControl(config, args.Length > 1 ? args[1] : "results/ffmpeg-control"); return Environment.ExitCode; }
            if (args.Length > 0 && args[0] == "--static-regression") { FfmpegRegression.RunStaticLowBandwidth(config, args.Length > 1 ? args[1] : "results/ffmpeg-static"); return Environment.ExitCode; }
            if (args.Length > 0 && args[0] == "--cpu-presets-regression")
            {
                CpuPresetRegression.Run(config, args.Length > 1 ? args[1] : "results/cpu-presets.json").GetAwaiter().GetResult();
                return 0;
            }
            if (args.Length > 0 && args[0] == "--demo-regression")
            {
                var seconds = args.Length > 1 ? int.Parse(args[1]) : 10;
                var output = args.Length > 2 ? args[2] : "results/ffmpeg-demo.json";
                UiApp.RunController(config, seconds, output); return Environment.ExitCode;
            }
            config = AutoCodecProbe.SelectSender(config).Configuration;
            UiApp.RunController(config); return Environment.ExitCode;
        }
        catch (Exception ex)
        {
            SessionTrace.Error("process-startup", ex);
            Console.Error.WriteLine(ex);
            try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "startup-error.txt"), ex.ToString()); }
            catch (Exception logError) { Console.Error.WriteLine("Cannot write startup-error.txt: " + logError); }
            return 1;
        }
    }
}
