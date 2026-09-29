using Microsoft.Extensions.Logging.Abstractions;
using TomYum.Bancho;
using TomYum.Domain.Entities;
using TomYum.Domain.Services;
using TomYum.Persistence.OsuWeb;
using TomYum.Server.Bancho;
using Xunit;

namespace TomYum.Server.Tests.OsuWeb;

/// <summary>Private messages from website chat that stable shows at login, and marking them read.</summary>
public sealed class PrivateMessagesTests : OsuWebDatabaseTest
{
    [MySqlFact]
    public async Task Unread_ComeAtLogin_UntilTheConversationIsRead()
    {
        var users = new OsuWebUserRepository(NewOsuWebContext());
        var hasher = new BcryptPasswordHasher();
        var me = await users.AddAsync(new User { Username = "Me", SafeName = "me", Email = "m@x.dev", PasswordBcrypt = hasher.HashPassword(hasher.ComputeMd5Hex("password")), Country = "nl" });
        var friend = await users.AddAsync(new User { Username = "Friend", SafeName = "friend", Email = "f@x.dev", PasswordBcrypt = "x", Country = "nl" });
        var other = await users.AddAsync(new User { Username = "Other", SafeName = "other", Email = "o@x.dev", PasswordBcrypt = "x", Country = "nl" });

        // With Friend: one already read, two new, and my own reply; with Other: one new (a /me).
        await ExecuteAsync($"""
            INSERT INTO channels (channel_id, name, type) VALUES (1, '#pm_{me.Id}-{friend.Id}', 'PM'), (2, '#pm_{me.Id}-{other.Id}', 'PM'), (3, '#osu', 'PUBLIC');
            INSERT INTO user_channels (user_id, channel_id, last_read_id) VALUES ({me.Id}, 1, 1), ({friend.Id}, 1, 4), ({me.Id}, 2, NULL), ({me.Id}, 3, NULL);
            INSERT INTO messages (message_id, user_id, channel_id, content, is_action) VALUES
              (1, {friend.Id}, 1, 'old', 0), (2, {friend.Id}, 1, 'are you there?', 0), (3, {friend.Id}, 1, 'play with me', 0),
              (4, {me.Id}, 1, 'later', 0), (5, {other.Id}, 2, 'waves', 1), (6, {other.Id}, 3, 'hi #osu', 0);
            """);
        var messages = new OsuWebPrivateMessages(NewOsuWebContext(), OsuWebDatabase);

        Assert.Equal(["are you there?", "play with me", "waves"], (await messages.UnreadAsync(me.Id, 50)).Select(m => m.Content));
        Assert.Equal(["play with me", "waves"], (await messages.UnreadAsync(me.Id, 2)).Select(m => m.Content));   // the newest

        // At login: BanchoBot's note, then the messages from their senders.
        var login = new BanchoLoginHandler(users, hasher, new InMemoryUserStatsRepository(), new PlayerRegistry(), new ChannelRegistry(),
            NullLogger<BanchoLoginHandler>.Instance, privateMessages: messages);
        var outcome = await login.HandleAsync($"Me\n{hasher.ComputeMd5Hex("password")}\nb20231228.1|0|0|abc:runningunderwine:def:ghi:jkl:|0\n", "TH", CancellationToken.None);
        Assert.Equal(
            [("BanchoBot", "You have unread messages from Friend, Other."), ("Friend", "are you there?"), ("Friend", "play with me"), ("Other", "\u0001ACTION waves\u0001")],
            Messages(outcome.Body));

        // Opening the conversation with Friend (osu-markasread.php), then logging out.
        await messages.MarkConversationReadAsync(me.Id, friend.Id);
        Assert.Equal(["waves"], (await messages.UnreadAsync(me.Id, 50)).Select(m => m.Content));
        await messages.MarkAllPrivateReadAsync(me.Id);
        Assert.Empty(await messages.UnreadAsync(me.Id, 50));
        Assert.Equal(4, await ScalarAsync<int>($"SELECT last_read_id FROM user_channels WHERE user_id = {friend.Id} AND channel_id = 1"));   // theirs untouched

        await messages.MarkChannelReadAsync(me.Id, "#osu");
        Assert.Equal(6, await ScalarAsync<int>($"SELECT last_read_id FROM user_channels WHERE user_id = {me.Id} AND channel_id = 3"));
    }

    private static List<(string Sender, string Text)> Messages(byte[] body)
    {
        var result = new List<(string, string)>();
        var reader = new BanchoPacketReader(body);
        while (reader.CanReadHeader)
        {
            var header = reader.ReadHeader();
            if ((ushort)header.Id != (ushort)ServerPacketId.SendMessage)
            {
                reader.Skip(checked((int)header.Length));
                continue;
            }
            var sender = reader.ReadString();
            var text = reader.ReadString();
            _ = reader.ReadString();
            _ = reader.ReadInt32();
            result.Add((sender, text));
        }
        return result;
    }
}
