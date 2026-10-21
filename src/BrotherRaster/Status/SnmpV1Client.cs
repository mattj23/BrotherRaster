using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace BrotherRaster.Status;

/// <summary>A value from a GET response. Integers of every SNMP application type land in <see cref="Integer"/>.</summary>
public sealed record SnmpValue(byte Tag, long? Integer, byte[]? Bytes)
{
    public string? Text => Bytes is null ? null : System.Text.Encoding.ASCII.GetString(Bytes);
}

/// <summary>
/// The smallest SNMPv1 client that reads printer status: GET only, one community string, no traps, no
/// walks. Brother label printers answer v1 <c>public</c> and nothing more is needed,
/// so an SNMP package would be more surface than it is worth.
/// </summary>
public sealed class SnmpV1Client(string host, string community = "public", int port = 161)
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(2);

    public int Retries { get; init; } = 1;

    /// <summary>
    /// Reads every OID in one request. v1 fails a whole request when one OID does not exist
    /// (noSuchName), so a missing OID is dropped and the rest are asked again; it is simply absent from
    /// the result. Returns <c>null</c> when the agent never answers.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, SnmpValue>?> GetAsync(IReadOnlyList<string> oids, CancellationToken cancellationToken = default)
    {
        var endpoint = new IPEndPoint(IPAddress.TryParse(host, out var ip) ? ip : (await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, cancellationToken))[0], port);
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        var pending = oids.ToList();

        while (pending.Count > 0)
        {
            var response = await ExchangeAsync(udp, endpoint, pending, cancellationToken);
            if (response is null)
            {
                return null;
            }

            if (response.ErrorStatus == NoSuchName && response.ErrorIndex >= 1 && response.ErrorIndex <= pending.Count)
            {
                pending.RemoveAt(response.ErrorIndex - 1);
                continue;
            }

            if (response.ErrorStatus != 0)
            {
                throw new InvalidDataException($"SNMP agent {host} returned error-status {response.ErrorStatus} (index {response.ErrorIndex}).");
            }

            return response.Bindings.ToDictionary();
        }

        return new Dictionary<string, SnmpValue>();
    }

    private const int NoSuchName = 2;

    private async Task<SnmpBer.Response?> ExchangeAsync(UdpClient udp, IPEndPoint endpoint, IReadOnlyList<string> oids, CancellationToken cancellationToken)
    {
        var requestId = RandomNumberGenerator.GetInt32(1, int.MaxValue);
        var request = SnmpBer.EncodeGetRequest(community, requestId, oids);

        for (var attempt = 0; attempt <= Retries; attempt++)
        {
            await udp.SendAsync(request, endpoint, cancellationToken);
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            wait.CancelAfter(Timeout);
            try
            {
                while (true)
                {
                    var datagram = await udp.ReceiveAsync(wait.Token);
                    var response = SnmpBer.TryDecodeResponse(datagram.Buffer);
                    if (response?.RequestId == requestId)
                    {
                        return response;
                    }
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
            catch (SocketException)
            {
                // ICMP port unreachable surfaces here on Linux: the host is up but has no agent.
                return null;
            }
        }

        return null;
    }
}

/// <summary>BER encoding for the handful of types SNMPv1 GET uses.</summary>
internal static class SnmpBer
{
    public const byte Integer = 0x02;
    public const byte OctetString = 0x04;
    public const byte Null = 0x05;
    public const byte ObjectId = 0x06;
    public const byte Sequence = 0x30;
    public const byte GetRequest = 0xA0;
    public const byte GetResponse = 0xA2;

    /// <summary>A decoded PDU. Bindings keep their order: v1 error indexes point into it.</summary>
    public sealed record Response(int RequestId, int ErrorStatus, int ErrorIndex, IReadOnlyList<KeyValuePair<string, SnmpValue>> Bindings);

    public static byte[] EncodeGetRequest(string community, int requestId, IReadOnlyList<string> oids)
    {
        var bindings = oids.Select(oid => Tlv(Sequence, [.. Tlv(ObjectId, EncodeOid(oid)), .. Tlv(Null, [])]));
        var pdu = Tlv(GetRequest, [
            .. Tlv(Integer, EncodeInteger(requestId)),
            .. Tlv(Integer, EncodeInteger(0)),
            .. Tlv(Integer, EncodeInteger(0)),
            .. Tlv(Sequence, bindings.SelectMany(b => b).ToArray()),
        ]);
        return Tlv(Sequence, [
            .. Tlv(Integer, EncodeInteger(0)), // version-1
            .. Tlv(OctetString, System.Text.Encoding.ASCII.GetBytes(community)),
            .. pdu,
        ]);
    }

    /// <summary>Encodes a GetResponse. Only tests and the fake agent use it.</summary>
    public static byte[] EncodeGetResponse(string community, int requestId, int errorStatus, int errorIndex, IEnumerable<(string Oid, byte Tag, byte[] Content)> bindings)
    {
        var encoded = bindings.SelectMany(b => Tlv(Sequence, [.. Tlv(ObjectId, EncodeOid(b.Oid)), .. Tlv(b.Tag, b.Content)])).ToArray();
        return Tlv(Sequence, [
            .. Tlv(Integer, EncodeInteger(0)),
            .. Tlv(OctetString, System.Text.Encoding.ASCII.GetBytes(community)),
            .. Tlv(GetResponse, [
                .. Tlv(Integer, EncodeInteger(requestId)),
                .. Tlv(Integer, EncodeInteger(errorStatus)),
                .. Tlv(Integer, EncodeInteger(errorIndex)),
                .. Tlv(Sequence, encoded),
            ]),
        ]);
    }

    public static Response? TryDecodeResponse(ReadOnlySpan<byte> datagram) => TryDecode(datagram, GetResponse);

    public static Response? TryDecodeRequest(ReadOnlySpan<byte> datagram) => TryDecode(datagram, GetRequest);

    private static Response? TryDecode(ReadOnlySpan<byte> datagram, byte pduTag)
    {
        try
        {
            var reader = new Reader(datagram.ToArray());
            var message = reader.Expect(Sequence);
            message.Expect(Integer);
            message.Expect(OctetString);
            var pdu = message.Expect(pduTag);
            var requestId = (int)pdu.ReadInteger();
            var errorStatus = (int)pdu.ReadInteger();
            var errorIndex = (int)pdu.ReadInteger();
            var list = pdu.Expect(Sequence);
            var bindings = new List<KeyValuePair<string, SnmpValue>>();
            while (!list.AtEnd)
            {
                var binding = list.Expect(Sequence);
                var oid = DecodeOid(binding.Expect(ObjectId).Remaining);
                var (tag, content) = binding.Read();
                bindings.Add(new(oid, tag switch
                {
                    Integer => new SnmpValue(tag, DecodeInteger(content), null),
                    >= 0x41 and <= 0x43 or 0x46 => new SnmpValue(tag, DecodeUnsigned(content), null), // Counter, Gauge, TimeTicks, Counter64
                    Null => new SnmpValue(tag, null, null),
                    _ => new SnmpValue(tag, null, content),
                }));
            }

            return new Response(requestId, errorStatus, errorIndex, bindings);
        }
        catch (Exception e) when (e is InvalidDataException or IndexOutOfRangeException or ArgumentOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    public static byte[] Tlv(byte tag, byte[] content)
    {
        byte[] length = content.Length switch
        {
            < 0x80 => [(byte)content.Length],
            <= 0xFF => [0x81, (byte)content.Length],
            _ => [0x82, (byte)(content.Length >> 8), (byte)content.Length],
        };
        return [tag, .. length, .. content];
    }

    public static byte[] EncodeInteger(long value)
    {
        var bytes = new List<byte>();
        do
        {
            bytes.Insert(0, (byte)value);
            value >>= 8;
        }
        while (!(value == 0 && (bytes[0] & 0x80) == 0) && !(value == -1 && (bytes[0] & 0x80) != 0));

        return [.. bytes];
    }

    public static byte[] EncodeOid(string oid)
    {
        var parts = oid.Split('.').Select(ulong.Parse).ToArray();
        if (parts.Length < 2)
        {
            throw new ArgumentException($"OID '{oid}' needs at least two components.", nameof(oid));
        }

        var bytes = new List<byte>();
        foreach (var arc in (ulong[])[parts[0] * 40 + parts[1], .. parts[2..]])
        {
            var chunk = new Stack<byte>();
            var v = arc;
            chunk.Push((byte)(v & 0x7F));
            while ((v >>= 7) > 0)
            {
                chunk.Push((byte)(0x80 | (v & 0x7F)));
            }

            bytes.AddRange(chunk);
        }

        return [.. bytes];
    }

    private static string DecodeOid(ReadOnlySpan<byte> content)
    {
        var arcs = new List<ulong>();
        ulong v = 0;
        foreach (var b in content)
        {
            v = checked((v << 7) | (uint)(b & 0x7F));
            if ((b & 0x80) == 0)
            {
                arcs.Add(v);
                v = 0;
            }
        }

        if (arcs.Count == 0)
        {
            throw new InvalidDataException("Empty OID.");
        }

        var first = arcs[0];
        var head = first < 80 ? new[] { first / 40, first % 40 } : [2, first - 80];
        return string.Join('.', head.Concat(arcs.Skip(1)));
    }

    private static long DecodeInteger(byte[] content)
    {
        if (content.Length is 0 or > 8)
        {
            throw new InvalidDataException("Bad INTEGER length.");
        }

        long v = (sbyte)content[0];
        foreach (var b in content.AsSpan(1))
        {
            v = (v << 8) | b;
        }

        return v;
    }

    private static long DecodeUnsigned(byte[] content)
    {
        ulong v = 0;
        foreach (var b in content)
        {
            v = checked((v << 8) | b);
        }

        return checked((long)v);
    }

    private sealed class Reader(byte[] data, int start = 0, int end = -1)
    {
        private int _position = start;
        private readonly int _end = end < 0 ? data.Length : end;

        public bool AtEnd => _position >= _end;

        public ReadOnlySpan<byte> Remaining => data.AsSpan(_position, _end - _position);

        public (byte Tag, byte[] Content) Read()
        {
            var (tag, offset, length) = Header();
            _position = offset + length;
            return (tag, data[offset..(offset + length)]);
        }

        public Reader Expect(byte tag)
        {
            var (actual, offset, length) = Header();
            if (actual != tag)
            {
                throw new InvalidDataException($"Expected tag 0x{tag:X2}, found 0x{actual:X2}.");
            }

            _position = offset + length;
            return new Reader(data, offset, offset + length);
        }

        public long ReadInteger()
        {
            var r = Expect(Integer);
            return DecodeInteger(r.Remaining.ToArray());
        }

        private (byte Tag, int Offset, int Length) Header()
        {
            if (_position + 2 > _end)
            {
                throw new InvalidDataException("Truncated BER.");
            }

            var tag = data[_position];
            int length = data[_position + 1];
            var offset = _position + 2;
            if (length >= 0x80)
            {
                var count = length & 0x7F;
                if (count is 0 or > 3 || offset + count > _end)
                {
                    throw new InvalidDataException("Bad BER length.");
                }

                length = 0;
                for (var i = 0; i < count; i++)
                {
                    length = (length << 8) | data[offset++];
                }
            }

            if (offset + length > _end)
            {
                throw new InvalidDataException("BER value runs past its container.");
            }

            return (tag, offset, length);
        }
    }
}
