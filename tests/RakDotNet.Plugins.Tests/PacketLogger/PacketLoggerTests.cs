using System.Net;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins.Tests.PacketLogger;

// Expected lines are derived from PacketLogger.cpp: FormatLine's "%s,%s%s,%s,%s,%5u,%s,%u,%llu,%s,%s,%i,%i,%i,%i,%s,"
// (lines 88-105), OnAck's "%s,Rcv,Ack,%i,,,,%llu,%s,%s,,,,,," (line 146) and OnPushBackPacket's
// "%s,Lcl,PBP,,,%s,%i,%llu,%s,%s,,,,,," (line 163). WriteLog appends "\n" (line 198).
public sealed class PacketLoggerTests : IDisposable
{
    private static readonly SystemAddress Local = new(IPAddress.Parse("10.0.0.1"), 1000);
    private static readonly SystemAddress Remote = new(IPAddress.Parse("10.0.0.2"), 2000);

    private readonly StringWriter _output = new();
    private readonly PluginMesh _mesh = new();

    public void Dispose()
    {
        _mesh.Dispose();
        _output.Dispose();
    }

    [Fact]
    public void The_header_is_written_with_its_own_newline_and_the_logs()
    {
        var logger = new ClockedLogger(_output);

        logger.LogHeader();

        Assert.Equal(
            "Clock,S|R,Typ,Reliable#,Frm #,PktID,BitLn,Time     ,Local IP:Port   ,RemoteIP:Port,SPID,SPIN,SPCO,OI,Suffix,Miscellaneous\n\n",
            _output.ToString());
    }

    [Fact]
    public void A_line_names_the_message_id_pads_numbers_to_five_and_prints_unsigned_sentinels_as_minus_one()
    {
        var logger = new ClockedLogger(_output);

        var line = logger.FormatLine("Snd", "Raw", 12, 3, (byte)MessageId.ConnectedPong, 64, 1234, Local, Remote,
            uint.MaxValue, uint.MaxValue, uint.MaxValue, uint.MaxValue);

        Assert.Equal("clock,Snd,Raw,   12,    3,ID_CONNECTED_PONG,64,1234,10.0.0.1|1000,10.0.0.2|2000,-1,-1,-1,-1,,", line);
    }

    [Fact]
    public void A_user_message_id_is_printed_as_its_number()
    {
        // BaseIDTOString returns 0 from ID_USER_PACKET_ENUM up, so UserIDTOString's Itoa is used.
        var logger = new ClockedLogger(_output);

        var line = logger.FormatLine("Rcv", "Nrm", 0, 0, (byte)MessageId.UserPacketEnum + 2, 8, 0, Local, Remote, 0, 0, 0, 0);

        Assert.Equal("clock,Rcv,Nrm,    0,    0,136,8,0,10.0.0.1|1000,10.0.0.2|2000,0,0,0,0,,", line);
    }

    [Fact]
    public void Without_printing_ids_the_id_is_a_number_padded_to_five()
    {
        var logger = new ClockedLogger(_output) { PrintId = false };

        var line = logger.FormatLine("Rcv", "Nrm", 0, 0, (byte)MessageId.ConnectedPong, 8, 0, Local, Remote, 0, 0, 0, 0);

        Assert.Contains(",    0,    3,8,", line, StringComparison.Ordinal);
    }

    [Fact]
    public void A_split_part_is_named_as_such_an_unreliable_number_is_not_applicable_and_prefix_and_suffix_frame_the_line()
    {
        var logger = new ClockedLogger(_output) { Prefix = "P:", Suffix = "end" };

        var line = logger.FormatLine("Snd", "Nrm", uint.MaxValue, 7, (byte)MessageId.UserPacketEnum, 800, 5, Local, Remote, 4, 1, 3, 9);

        Assert.Equal("clock,P:Snd,Nrm,N/A,    7,(SPLIT PACKET),800,5,10.0.0.1|1000,10.0.0.2|2000,4,1,3,9,end,", line);
    }

    [Fact]
    public void Prefix_and_suffix_keep_at_most_255_characters()
    {
        var logger = new ClockedLogger(_output) { Prefix = new string('p', 300), Suffix = new string('s', 300) };

        Assert.Equal(255, logger.Prefix.Length);
        Assert.Equal(255, logger.Suffix.Length);
    }

    [Fact]
    public void Base_id_names_come_from_the_loggers_own_table()
    {
        Assert.Equal("ID_CONNECTED_PING", Plugins.PacketLogger.BaseIdToString(0));
        Assert.Equal("ID_SQLite3_EXEC", Plugins.PacketLogger.BaseIdToString((byte)MessageId.Sqlite3Exec));
        Assert.Equal("ID_RESERVED_9", Plugins.PacketLogger.BaseIdToString((byte)MessageId.Reserved9));
        Assert.Null(Plugins.PacketLogger.BaseIdToString((byte)MessageId.UserPacketEnum));
    }

    [Fact]
    public void Miscellaneous_writes_only_the_message_as_RakNet_does()
    {
        // PacketLogger.cpp:202-223 formats a full line and then logs msg.
        var logger = new ClockedLogger(_output);

        logger.WriteMiscellaneous("type", "the message");

        Assert.Equal("the message\n", _output.ToString());
    }

    [Fact]
    public void Sent_and_received_messages_and_acknowledgements_are_logged_between_connected_peers()
    {
        var clientLog = new StringWriter();
        var serverLog = new StringWriter();
        var server = _mesh.Add(maxIncoming: 1, new ClockedLogger(serverLog) { LogDirectMessages = false });
        var client = _mesh.Add(maxIncoming: 0, new ClockedLogger(clientLog) { LogDirectMessages = false });
        var connection = _mesh.Connect(client, server);

        connection.Send(new byte[] { (byte)MessageId.UserPacketEnum, 1, 2 }, Reliability.ReliableOrdered);
        _mesh.Settle();

        var serverAddress = server.Sockets[0].LocalAddress;
        var sent = Assert.Single(Lines(clientLog), l => l.StartsWith("clock,Snd,Nrm,", StringComparison.Ordinal) && l.Contains(",134,24,", StringComparison.Ordinal));
        var fields = sent.Split(',');
        Assert.Equal(connection.ExternalAddress.ToString(writePort: true), fields[8]);
        Assert.Equal(serverAddress.ToString(writePort: true), fields[9]);
        var messageNumber = int.Parse(fields[3].Trim(), System.Globalization.CultureInfo.InvariantCulture);
        Assert.Contains(Lines(clientLog), l => l.StartsWith($"clock,Rcv,Ack,{messageNumber},,,,", StringComparison.Ordinal)
            && l.EndsWith($",{connection.ExternalAddress.ToString(writePort: true)},{serverAddress.ToString(writePort: true)},,,,,,", StringComparison.Ordinal));
        Assert.Single(Lines(serverLog), l => l.StartsWith($"clock,Rcv,Nrm,{fields[3]},", StringComparison.Ordinal) && l.Contains(",134,24,", StringComparison.Ordinal));
    }

    [Fact]
    public void A_timestamped_message_is_logged_with_the_id_after_the_time()
    {
        var log = new StringWriter();
        var server = _mesh.Add(maxIncoming: 1);
        var client = _mesh.Add(maxIncoming: 0, new ClockedLogger(log) { LogDirectMessages = false });
        var connection = _mesh.Connect(client, server);

        connection.Send(new byte[] { (byte)MessageId.Timestamp, 0, 0, 0, 0, 0, 0, 0, 1, (byte)MessageId.UserPacketEnum }, Reliability.Reliable);
        _mesh.Settle();

        Assert.Single(Lines(log), l => l.StartsWith("clock,Snd,Tms,", StringComparison.Ordinal) && l.Contains(",134,80,", StringComparison.Ordinal));
    }

    [Fact]
    public void Datagrams_are_logged_raw_unless_direct_messages_are_off()
    {
        var log = new StringWriter();
        var quietLog = new StringWriter();
        var server = _mesh.Add(maxIncoming: 1, new ClockedLogger(log));
        var client = _mesh.Add(maxIncoming: 0, new ClockedLogger(quietLog) { LogDirectMessages = false });

        _mesh.Connect(client, server);

        // OPEN_CONNECTION_REQUEST_1 arrives raw first: Rcv with the first internal address as local.
        var first = Lines(log).First(l => l.Contains(",Raw,", StringComparison.Ordinal));
        Assert.StartsWith("clock,Rcv,Raw,    0,    0,ID_OPEN_CONNECTION_REQUEST_1,", first, StringComparison.Ordinal);
        Assert.EndsWith($",{server.InternalAddresses[0].ToString(writePort: true)},{client.Sockets[0].LocalAddress.ToString(writePort: true)},-1,-1,-1,-1,,", first, StringComparison.Ordinal);
        Assert.Contains(Lines(log), l => l.StartsWith("clock,Snd,Raw,    0,    0,ID_OPEN_CONNECTION_REPLY_1,", StringComparison.Ordinal));
        Assert.DoesNotContain(Lines(quietLog), l => l.Contains(",Raw,", StringComparison.Ordinal));
    }

    [Fact]
    public void A_reliability_layer_error_is_logged_with_its_message_as_the_type()
    {
        var log = new StringWriter();
        var server = _mesh.Add(maxIncoming: 1, new ClockedLogger(log) { LogDirectMessages = false });
        var client = _mesh.Add(maxIncoming: 0);
        _mesh.Connect(client, server);

        client.Sockets[0].SendTo(new byte[] { 0x7F, 0, 0, 0 }, server.Sockets[0].LocalAddress);
        _mesh.Settle();

        var line = Assert.Single(Lines(log), l => l.Contains("RcvErr", StringComparison.Ordinal));
        Assert.StartsWith("clock,RcvErr,dhf.isValid==false,    0,    0,,32,", line, StringComparison.Ordinal);
        Assert.EndsWith($",{server.InternalAddresses[0].ToString(writePort: true)},{server.Connections.Single().Address.ToString(writePort: true)},-1,-1,-1,-1,,", line, StringComparison.Ordinal);
    }

    [Fact]
    public void An_injected_packet_is_logged_as_a_local_push_back()
    {
        var log = new StringWriter();
        var peer = _mesh.Add(maxIncoming: 0, new ClockedLogger(log) { LogDirectMessages = false });

        peer.Inject(new byte[] { (byte)MessageId.AdvertiseSystem, 9 }, Remote, new RakNetGuid(5));
        peer.Inject(new byte[] { (byte)MessageId.UserPacketEnum }, Remote, new RakNetGuid(5));
        _mesh.Step();

        Assert.Equal(
            [
                $"clock,Lcl,PBP,,,ID_ADVERTISE_SYSTEM,16,{peer.GetTime()},UNASSIGNED_SYSTEM_ADDRESS,10.0.0.2|2000,,,,,,",
                // BaseIDTOString gives a null pointer for a user ID, which printf writes as (null).
                $"clock,Lcl,PBP,,,(null),8,{peer.GetTime()},UNASSIGNED_SYSTEM_ADDRESS,10.0.0.2|2000,,,,,,",
            ],
            Lines(log));
    }

    [Fact]
    public void A_logger_can_name_user_ids_itself()
    {
        var logger = new NamingLogger(_output);

        var line = logger.FormatLine("Rcv", "Nrm", 0, 0, (byte)MessageId.UserPacketEnum, 8, 0, Local, Remote, 0, 0, 0, 0);

        Assert.Contains(",ID_GAME_START,", line, StringComparison.Ordinal);
    }

    [Fact]
    public void The_threadsafe_logger_writes_what_the_io_cycle_logged_on_the_next_read()
    {
        var log = new StringWriter();
        var logger = new ThreadsafeClockedLogger(log);
        var peer = _mesh.Add(maxIncoming: 0, logger);
        var other = _mesh.Add(maxIncoming: 0);

        peer.PingUnconnected(other.Sockets[0].LocalAddress);
        peer.Update();

        Assert.Empty(log.ToString());

        peer.Events.TryRead(out _);

        Assert.StartsWith("clock,Snd,Raw,    0,    0,ID_UNCONNECTED_PING,", log.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_file_logger_writes_the_header_and_lines_to_a_csv_named_by_prefix_and_time()
    {
        var directory = Directory.CreateTempSubdirectory("packetlogger");
        try
        {
            string path;
            using (var logger = new ClockedFileLogger())
            {
                var peer = _mesh.Add(maxIncoming: 0, logger);
                logger.StartLog(Path.Combine(directory.FullName, "Run"));
                logger.WriteLog("a line");
                path = Assert.Single(Directory.GetFiles(directory.FullName));
                Assert.Equal($"Run_{(int)(uint)peer.GetTime()}.csv", Path.GetFileName(path));
            }

            Assert.Equal(
                "Clock,S|R,Typ,Reliable#,Frm #,PktID,BitLn,Time     ,Local IP:Port   ,RemoteIP:Port,SPID,SPIN,SPCO,OI,Suffix,Miscellaneous\n\na line\n",
                File.ReadAllText(path));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static List<string> Lines(StringWriter writer) =>
        [.. writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)];

    // GetLocalTime is empty off Windows (PacketLogger.cpp:276-311); a fixed clock column keeps lines comparable.
    private sealed class ClockedLogger(TextWriter writer) : Plugins.PacketLogger(writer)
    {
        protected override string GetLocalTime() => "clock";
    }

    private sealed class ThreadsafeClockedLogger(TextWriter writer) : ThreadsafePacketLogger(writer)
    {
        protected override string GetLocalTime() => "clock";
    }

    private sealed class ClockedFileLogger : PacketFileLogger
    {
        protected override string GetLocalTime() => "clock";
    }

    private sealed class NamingLogger(TextWriter writer) : Plugins.PacketLogger(writer)
    {
        protected override string UserIdToString(byte id) => id == (byte)MessageId.UserPacketEnum ? "ID_GAME_START" : base.UserIdToString(id);
    }
}
