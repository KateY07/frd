using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Frd;

static class FrdNetwork
{
    public static IPAddress Canonical(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
    public static bool IsLocalAddress(IPAddress address)
    {
        address = Canonical(address);
        if (IPAddress.IsLoopback(address)) return true;
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces().Any(adapter => adapter.GetIPProperties().UnicastAddresses
                .Any(local => Canonical(local.Address).Equals(address)));
        }
        catch (NetworkInformationException error)
        {
            Console.Error.WriteLine($"[input] Cannot determine whether peer shares this desktop; using the local test target: {error}");
            return true;
        }
    }
    public static bool SameEndpoint(EndPoint first, EndPoint second) => first is IPEndPoint a && second is IPEndPoint b &&
        a.Port == b.Port && Canonical(a.Address).Equals(Canonical(b.Address));
    public static int UdpOverhead(IPAddress address) => Canonical(address).AddressFamily == AddressFamily.InterNetworkV6 ? 48 : 28;
    public static IPAddress Any(AddressFamily family) => family == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any;
    public static IPAddress[] ListenAddresses(string value) => value.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        ? [IPAddress.Loopback, IPAddress.IPv6Loopback] : [Canonical(IPAddress.Parse(value))];
    public static async Task<TcpClient> ConnectAsync(string host, int port, CancellationToken token)
    {
        var addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, token);
        Exception? failure = null;
        foreach (var address in addresses.Select(Canonical).Distinct())
        {
            var client = new TcpClient(address.AddressFamily) { NoDelay = true };
            try { await client.ConnectAsync(address, port, token); return client; }
            catch (SocketException error)
            {
                client.Dispose(); failure = error;
                Console.Error.WriteLine($"[connect] {address}:{port}: {error.Message}");
            }
            catch { client.Dispose(); throw; }
        }
        throw new IOException($"Cannot connect to {host}:{port}.", failure);
    }
}
