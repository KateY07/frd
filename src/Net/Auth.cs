using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Frd;

/// <summary>The host speaks first: a random nonce the controller must answer with a proof that it knows the token. The token itself never crosses the network.</summary>
sealed record RemoteChallenge(int Version, string Nonce);

/// <summary>
/// Challenge-response authentication with mutual proof, and the per-session keys derived from it.
/// <list type="bullet">
/// <item>key = PBKDF2-SHA256(token, hostNonce): a sniffed exchange cannot be replayed (fresh nonce) and an offline guess costs the same work as an online one.</item>
/// <item>The controller proves itself with HMAC(key, "client" | nonces | kind | session); the host answers with HMAC(key, "server" | ...), so a rogue host learns nothing and is detected.</item>
/// <item>The session key of the first ("hello") connection signs the UDP keyboard/mouse datagrams. Video and the TCP channels are authenticated but not encrypted.</item>
/// </list>
/// </summary>
static class RemoteAuth
{
    public const int Version = 3, NonceBytes = 32, Iterations = 50_000;

    public static byte[] DeriveKey(string token, byte[] hostNonce) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(token), hostNonce, Iterations, HashAlgorithmName.SHA256, 32);

    static byte[] Mac(byte[] key, string role, byte[] hostNonce, byte[] clientNonce, string kind, string session)
    {
        var data = Encoding.UTF8.GetBytes(role + "\0" + kind + "\0" + session + "\0").Concat(hostNonce).Concat(clientNonce).ToArray();
        return HMACSHA256.HashData(key, data);
    }

    public static string ClientProof(byte[] key, byte[] hostNonce, byte[] clientNonce, string kind, string session) =>
        Convert.ToHexString(Mac(key, "client", hostNonce, clientNonce, kind, session));

    public static string ServerProof(byte[] key, byte[] hostNonce, byte[] clientNonce, string kind, string session) =>
        Convert.ToHexString(Mac(key, "server", hostNonce, clientNonce, kind, session));

    public static byte[] SessionKey(byte[] key, byte[] hostNonce, byte[] clientNonce) => Mac(key, "session", hostNonce, clientNonce, "", "");

    public static byte[] InputKey(byte[] sessionKey) => HMACSHA256.HashData(sessionKey, "input"u8);

    static bool TryHex(string text, int length, out byte[] bytes)
    {
        bytes = [];
        if (text.Length != length * 2 || !text.All(Uri.IsHexDigit)) return false;
        bytes = Convert.FromHexString(text);
        return true;
    }

    /// <summary>Host side: checks the controller's proof. On success returns the key material for the reply and, for the first connection, the session.</summary>
    public static bool VerifyClient(string token, byte[] hostNonce, RemoteRequest request, out byte[] key, out byte[] clientNonce)
    {
        key = []; clientNonce = [];
        if (string.IsNullOrWhiteSpace(token) || !TryHex(request.ClientNonce, NonceBytes, out clientNonce) || !TryHex(request.Proof, 32, out var proof)) return false;
        key = DeriveKey(token, hostNonce);
        var expected = Mac(key, "client", hostNonce, clientNonce, request.Kind, request.Session);
        return CryptographicOperations.FixedTimeEquals(expected, proof);
    }

    /// <summary>Controller side: answers the host's challenge, sends <paramref name="request"/> and verifies the host's proof in the reply.</summary>
    public static async Task<(RemoteReply Reply, byte[] SessionKey)> HandshakeAsync(NetworkStream stream, string token, RemoteRequest request, CancellationToken cancellation)
    {
        var challenge = await RemoteWire.ReadAsync<RemoteChallenge>(stream, cancellation);
        if (challenge.Version != Version || !TryHex(challenge.Nonce, NonceBytes, out var hostNonce))
            throw new IOException("The host uses a different connection protocol; update both endpoints to the same version.");
        var key = DeriveKey(token, hostNonce);
        var clientNonce = RandomNumberGenerator.GetBytes(NonceBytes);
        await RemoteWire.WriteAsync(stream, request with
        {
            ClientNonce = Convert.ToHexString(clientNonce),
            Proof = ClientProof(key, hostNonce, clientNonce, request.Kind, request.Session)
        }, cancellation);
        var reply = await RemoteWire.ReadAsync<RemoteReply>(stream, cancellation);
        if (!reply.Success) return (reply, []);   // the caller reports the host's message
        if (!TryHex(reply.ServerProof, 32, out var proof) ||
            !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(ServerProof(key, hostNonce, clientNonce, request.Kind, request.Session)), proof))
            throw new IOException("The host did not prove that it knows the connection token.");
        return (reply, SessionKey(key, hostNonce, clientNonce));
    }
}

/// <summary>Slows down password guessing: failed attempts from one address back off exponentially, and only a few unauthenticated handshakes run at once.</summary>
sealed class AuthThrottle
{
    const int MaximumHandshakes = 16, MaximumBackoffSeconds = 60;
    readonly ConcurrentDictionary<IPAddress, (int Failures, long BlockedUntil, long Last)> state = new();
    int handshakes;

    static long Now => Environment.TickCount64;

    /// <summary>Returns false (and the caller closes the connection) while the address is backing off or too many handshakes are in flight.</summary>
    public bool TryBegin(IPAddress address)
    {
        if (state.TryGetValue(address, out var entry) && entry.BlockedUntil > Now) return false;
        if (Interlocked.Increment(ref handshakes) > MaximumHandshakes) { Interlocked.Decrement(ref handshakes); return false; }
        return true;
    }

    public void End() => Interlocked.Decrement(ref handshakes);

    public void Failed(IPAddress address)
    {
        var now = Now;
        state.AddOrUpdate(address, (1, now + 1000, now), (_, old) =>
        {
            var failures = now - old.Last > 600_000 ? 1 : old.Failures + 1;
            var seconds = Math.Min(MaximumBackoffSeconds, 1 << Math.Min(failures - 1, 6));
            return (failures, now + seconds * 1000L, now);
        });
        if (state.Count > 1024) foreach (var pair in state) if (now - pair.Value.Last > 600_000) state.TryRemove(pair.Key, out _);
    }

    public void Succeeded(IPAddress address) => state.TryRemove(address, out _);
}
