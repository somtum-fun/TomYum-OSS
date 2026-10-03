using TomYum.Bancho;
using TomYum.Persistence.OsuWeb;

namespace TomYum.Server.Bancho.Packets;

/// <summary>
/// BEATMAP_INFO_REQUEST (68): what old builds' song select asks bancho for (docs/old-clients.md;
/// today's client uses /web/osu-getbeatmapinfo.php): the maps it has, by file name and by id. The
/// answer, BEATMAP_INFO_REPLY (69), gives each known one its ids, status and the player's best grade
/// per mode. Layout from Lekuruu's chio (b490 request, b20121028 reply, b388/b20150826/b20161101 statuses).
/// </summary>
public sealed class BeatmapInfoRequestHandler : IClientPacketHandler
{
    /// <summary>At most this many maps per request (osuTitanic's limit).</summary>
    internal const int MaxMaps = 4500;

    /// <inheritdoc />
    public ClientPacketId PacketId => ClientPacketId.BeatmapInfoRequest;

    /// <inheritdoc />
    public async Task HandleAsync(ClientPacketContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Services.GetService<OsuWebStableClient>() is not { } website)
            return;
        if (Read(context.Payload.Span) is not var (filenames, ids))
            return;

        var byName = (await website.FindByFilenamesAsync(filenames, cancellationToken).ConfigureAwait(false))
            .GroupBy(m => m.Filename, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var byId = (await website.FindByIdsAsync(ids, cancellationToken).ConfigureAwait(false))
            .ToDictionary(m => (int)m.BeatmapId);

        var maps = new List<(short Index, FileMap Map)>();
        for (var i = 0; i < filenames.Count; i++)
            if (byName.TryGetValue(filenames[i], out var map))
                maps.Add(((short)Math.Min(i, short.MaxValue), map));
        foreach (var id in ids)
            if (byId.TryGetValue(id, out var map))
                maps.Add((-1, map));

        var grades = await website.BestGradesAsync(context.Player.UserId, maps.Select(m => m.Map.BeatmapId).Distinct().ToList(), cancellationToken)
            .ConfigureAwait(false);
        var build = context.Player.Build;
        context.Response.WritePacket(ServerPacketId.BeatmapInfoReply, w =>
        {
            w.WriteInt32(maps.Count);
            foreach (var (index, map) in maps)
            {
                w.WriteInt16(index);
                w.WriteInt32((int)map.BeatmapId);
                w.WriteInt32((int)map.BeatmapsetId);
                w.WriteInt32(0); // forum thread
                w.WriteSByte(Status(map.Approved, build));
                // The player's grades in the order the client reads them: osu!, catch, taiko, mania.
                foreach (var ruleset in (int[])[0, 2, 1, 3])
                    w.WriteSByte(Grade(grades.TryGetValue((map.BeatmapId, ruleset), out var grade) ? grade : null));
                w.WriteString(map.Checksum);
            }
        });
    }

    /// <summary>The file names and ids asked for, or null when the payload isn't a request.</summary>
    internal static (List<string> Filenames, List<int> Ids)? Read(ReadOnlySpan<byte> payload)
    {
        try
        {
            var reader = new BanchoPacketReader(payload);
            var filenames = new List<string>();
            var count = reader.ReadInt32();
            for (var i = 0; i < count && i < MaxMaps; i++)
                filenames.Add(reader.ReadString());
            var ids = new List<int>();
            if (reader.Remaining >= 4)
            {
                count = reader.ReadInt32();
                for (var i = 0; i < count && i < MaxMaps && reader.Remaining >= 4; i++)
                    ids.Add(reader.ReadInt32());
            }
            return (filenames, ids);
        }
        catch (InvalidDataException)
        {
            return null;
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    /// <summary>
    /// The website's status (-2 graveyard … 4 loved) as <paramref name="build"/> knows them: unranked ones
    /// are pending; qualified exists from b20150826, loved from b20161101 (approved before).
    /// </summary>
    internal static sbyte Status(int approved, ClientBuild build) => approved switch
    {
        <= 0 => 0,
        1 => 1,
        2 => 2,
        3 => build.Date is { } date && date < new DateOnly(2015, 8, 26) ? (sbyte)2 : (sbyte)3,
        4 => build.Date is { } date && date < new DateOnly(2016, 11, 1) ? (sbyte)2 : (sbyte)4,
        _ => 0,
    };

    /// <summary>A grade as the client numbers it: XH 0, SH 1, X 2, S 3, A 4, B 5, C 6, D 7, F 8, none 9.</summary>
    internal static sbyte Grade(string? grade) => grade?.ToUpperInvariant() switch
    {
        "XH" => 0,
        "SH" => 1,
        "X" or "SS" => 2,
        "S" => 3,
        "A" => 4,
        "B" => 5,
        "C" => 6,
        "D" => 7,
        "F" => 8,
        _ => 9,
    };
}
