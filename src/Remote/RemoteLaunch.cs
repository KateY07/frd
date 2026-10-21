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

static partial class RemoteLaunch
{
    internal static int hostExitCode;
    public const string Usage = "FRD CLI\n安全：口令不会在网络上发送，键鼠包带签名；FRD 本身不加密内容，设计上依赖已加密的底层链路（VPN、隧道或专网）。\n被控：FRD.exe --host --listen localhost --port 5000 --token 自定口令\n主控：FRD.exe --connect 被控IP或主机名 --port 5000 --token 相同口令\n每次启动被控端都必须显式指定 --listen、--port、--token；无默认监听地址或口令。\n--listen localhost：仅 IPv4/IPv6 回环；::：所有网卡双栈；也可指定具体 IPv4/IPv6 地址。\n--help：文本帮助；--version：版本；--help-window：帮助窗口。\n无参数：localhost Demo。远程模式只使用 --port：被控端在该端口同时绑定 TCP/UDP；主控的全部 TCP 连接和 UDP 数据报均以该端口为目标。视频、反馈、诊断和键鼠复用同一个 UDP socket。";
    public static void Help(bool window)
    {
        Console.WriteLine(Usage);
        if (window) ShowMessage(Usage);
    }
    public sealed record Options(bool Host, string? Peer, IPAddress[] Addresses, int Port, string Token,
        int Seconds, string? Report, string? InteractionScript);
    public static Options Parse(string[] args)
    {
        var host = args[0] == "--host";
        if (!host && args.Length < 2) throw new ArgumentException(Usage);
        var values = new Dictionary<string, string>();
        for (var i = host ? 1 : 2; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length || args[i] is not ("--port" or "--token" or "--listen" or "--test-seconds" or "--report" or "--interaction-script") || !values.TryAdd(args[i], args[i + 1]))
                throw new ArgumentException(Usage);
        }
        if (!values.TryGetValue("--token", out var token) || string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("必须每次通过命令行 --token 指定非空连接口令；没有默认口令，也不会从配置文件读取口令。\n" + Usage);
        if (!values.TryGetValue("--port", out var portText) || !int.TryParse(portText, out var port) || port is < 1 or > 65535)
            throw new ArgumentException(Usage);
        if (!host)
        {
            if (values.ContainsKey("--listen") || string.IsNullOrWhiteSpace(args[1])) throw new ArgumentException(Usage);
            var seconds = int.Parse(values.GetValueOrDefault("--test-seconds", "0"));
            if (seconds is < 0 or > 3600) throw new ArgumentException("Invalid test duration.");
            return new(false, args[1], [], port, token, seconds, values.GetValueOrDefault("--report"), values.GetValueOrDefault("--interaction-script"));
        }
        if (!values.TryGetValue("--listen", out var listen) || string.IsNullOrWhiteSpace(listen))
            throw new ArgumentException("被控端必须通过 --listen 明确指定 localhost 或 IPv4/IPv6 监听地址；不默认监听所有网卡。\n" + Usage);
        if (values.Keys.Any(key => key is "--test-seconds" or "--report" or "--interaction-script")) throw new ArgumentException(Usage);
        return new(true, null, FrdNetwork.ListenAddresses(listen), port, token, 0, null, null);
    }
    public static int Run(AppConfiguration config, Options options, CodecProbeSample[]? probeSamples = null)
    {
        if (!options.Host)
        {
            UiApp.RunController(config, options.Seconds, options.Report, new(options.Peer!, options.Port, options.Token));
            return Environment.ExitCode;
        }
        return UiApp.RunHost(config, options.Addresses, options.Port, options.Token, probeSamples);
    }

    static void ShowMessage(string text)
    {
        UiNative.MessageBox(0, text, "FRD 命令行", 0x40 /* MB_ICONINFORMATION */);
    }
}
