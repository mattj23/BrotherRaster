using System.Net;
using System.Net.Sockets;
using BrotherRaster.Status;

namespace BrotherRaster.Tests;

public sealed class SnmpTests
{
    [Theory]
    [InlineData("1.3.6.1.2.1.1.1.0", "2B06010201010100")]
    [InlineData("1.3.6.1.2.1.43.16.5.1.2.1.1", "2B060102012B100501020101")]
    [InlineData("1.3.6.1.4.1.2435.2.3.9.4.2.1.5.5.1.0", "2B06010401930302030904020105050100")]
    public void Oids_encode_like_net_snmp(string oid, string hex) =>
        Assert.Equal(hex, Convert.ToHexString(SnmpBer.EncodeOid(oid)));

    [Theory]
    [InlineData(0, "00")]
    [InlineData(127, "7F")]
    [InlineData(128, "0080")]
    [InlineData(256, "0100")]
    [InlineData(-1, "FF")]
    [InlineData(-129, "FF7F")]
    [InlineData(int.MaxValue, "7FFFFFFF")]
    public void Integers_use_minimal_twos_complement(long value, string hex) =>
        Assert.Equal(hex, Convert.ToHexString(SnmpBer.EncodeInteger(value)));

    [Fact]
    public void Get_request_matches_a_known_packet()
    {
        // snmpget -v1 -c public host 1.3.6.1.2.1.1.1.0, request-id 1.
        var packet = SnmpBer.EncodeGetRequest("public", 1, ["1.3.6.1.2.1.1.1.0"]);
        Assert.Equal("302602010004067075626C6963A019020101020100020100300E300C06082B060102010101000500", Convert.ToHexString(packet));
    }

    [Fact]
    public void Response_values_decode_by_type()
    {
        var packet = SnmpBer.EncodeGetResponse("public", 77, 0, 0, [
            ("1.3.6.1.2.1.25.3.2.1.5.1", SnmpBer.Integer, SnmpBer.EncodeInteger(5)),
            ("1.3.6.1.2.1.25.3.5.1.2.1", SnmpBer.OctetString, [0x08]),
            ("1.3.6.1.2.1.43.16.5.1.2.1.1", SnmpBer.OctetString, "COVER OPEN      "u8.ToArray()),
            ("1.3.6.1.2.1.1.3.0", 0x43, [0x00, 0xF4, 0x24, 0x00]), // TimeTicks, top bit clear
            ("1.3.6.1.2.1.2.2.1.10.1", 0x41, [0x00, 0xFF, 0xFF, 0xFF, 0xFF]), // Counter32 4294967295
        ]);

        var response = SnmpBer.TryDecodeResponse(packet)!;
        var values = response.Bindings.ToDictionary();
        Assert.Equal(77, response.RequestId);
        Assert.Equal(5, values["1.3.6.1.2.1.25.3.2.1.5.1"].Integer);
        Assert.Equal([0x08], values["1.3.6.1.2.1.25.3.5.1.2.1"].Bytes);
        Assert.Equal("COVER OPEN      ", values["1.3.6.1.2.1.43.16.5.1.2.1.1"].Text);
        Assert.Equal(16000000, values["1.3.6.1.2.1.1.3.0"].Integer);
        Assert.Equal(4294967295, values["1.3.6.1.2.1.2.2.1.10.1"].Integer);
    }

    [Fact]
    public void Long_form_lengths_round_trip()
    {
        var longText = new string('x', 300);
        var packet = SnmpBer.EncodeGetResponse("public", 1, 0, 0, [("1.3.6.1.2.1.1.1.0", SnmpBer.OctetString, System.Text.Encoding.ASCII.GetBytes(longText))]);
        Assert.Equal(longText, SnmpBer.TryDecodeResponse(packet)!.Bindings.Single().Value.Text);
    }

    [Fact]
    public void Garbage_and_truncated_packets_are_ignored()
    {
        var packet = SnmpBer.EncodeGetResponse("public", 1, 0, 0, [("1.3.6.1.2.1.1.1.0", SnmpBer.OctetString, "hello"u8.ToArray())]);
        for (var cut = 0; cut < packet.Length; cut++)
        {
            Assert.Null(SnmpBer.TryDecodeResponse(packet.AsSpan(0, cut)));
        }

        Assert.Null(SnmpBer.TryDecodeResponse([0xFF, 0x01, 0x02]));
        Assert.Null(SnmpBer.TryDecodeRequest(packet)); // a response is not a request
    }

    [Fact]
    public async Task Client_reads_values_from_an_agent()
    {
        using var agent = new FakeAgent(new Dictionary<string, (byte, byte[])>
        {
            ["1.3.6.1.2.1.25.3.5.1.1.1"] = (SnmpBer.Integer, SnmpBer.EncodeInteger(3)),
            ["1.3.6.1.2.1.43.8.2.1.12.1.1"] = (SnmpBer.OctetString, "62mm / 2.4\""u8.ToArray()),
        });

        var values = await new SnmpV1Client("127.0.0.1", port: agent.Port).GetAsync(["1.3.6.1.2.1.25.3.5.1.1.1", "1.3.6.1.2.1.43.8.2.1.12.1.1"]);

        Assert.NotNull(values);
        Assert.Equal(3, values["1.3.6.1.2.1.25.3.5.1.1.1"].Integer);
        Assert.Equal("62mm / 2.4\"", values["1.3.6.1.2.1.43.8.2.1.12.1.1"].Text);
    }

    [Fact]
    public async Task Client_drops_an_oid_the_agent_does_not_have()
    {
        using var agent = new FakeAgent(new Dictionary<string, (byte, byte[])>
        {
            ["1.3.6.1.2.1.25.3.5.1.1.1"] = (SnmpBer.Integer, SnmpBer.EncodeInteger(3)),
        });

        var values = await new SnmpV1Client("127.0.0.1", port: agent.Port).GetAsync(["1.3.6.1.2.1.43.18.1.1.8.1.1", "1.3.6.1.2.1.25.3.5.1.1.1"]);

        Assert.NotNull(values);
        Assert.Equal(["1.3.6.1.2.1.25.3.5.1.1.1"], values.Keys);
        Assert.Equal(2, agent.Requests);
    }

    [Fact]
    public async Task Client_returns_null_when_nothing_answers()
    {
        // A bound socket that never replies: the printer-is-off case.
        using var silent = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)silent.Client.LocalEndPoint!).Port;
        var client = new SnmpV1Client("127.0.0.1", port: port) { Timeout = TimeSpan.FromMilliseconds(100), Retries = 1 };

        Assert.Null(await client.GetAsync(["1.3.6.1.2.1.1.1.0"]));
    }

    /// <summary>A one-thread SNMPv1 agent on loopback that answers from a fixed table.</summary>
    private sealed class FakeAgent : IDisposable
    {
        private readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly CancellationTokenSource _stop = new();
        private int _requests;

        public FakeAgent(IReadOnlyDictionary<string, (byte Tag, byte[] Content)> table)
        {
            _ = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    var datagram = await _udp.ReceiveAsync(_stop.Token);
                    Interlocked.Increment(ref _requests);
                    var request = SnmpBer.TryDecodeRequest(datagram.Buffer)!;
                    var oids = request.Bindings.Select(b => b.Key).ToList();
                    var missing = oids.FindIndex(o => !table.ContainsKey(o));
                    var reply = missing >= 0
                        ? SnmpBer.EncodeGetResponse("public", request.RequestId, 2, missing + 1, oids.Select(o => (o, SnmpBer.Null, Array.Empty<byte>())))
                        : SnmpBer.EncodeGetResponse("public", request.RequestId, 0, 0, oids.Select(o => (o, table[o].Tag, table[o].Content)));
                    await _udp.SendAsync(reply, datagram.RemoteEndPoint);
                }
            });
        }

        public int Port => ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;

        public int Requests => _requests;

        public void Dispose()
        {
            _stop.Cancel();
            _udp.Dispose();
        }
    }
}
