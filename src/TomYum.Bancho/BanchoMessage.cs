namespace TomYum.Bancho;

/// <summary>
/// A chat message on the osu!stable wire (the <c>message</c> composite, osu type 11): who sent
/// it, the text, the destination, and the sender's user id. The same shape carries both public
/// (channel) and private (user) messages — <see cref="Target"/> is a channel name like
/// <c>#osu</c> for public messages and a username for private ones.
/// </summary>
/// <remarks>
/// Layout (see <c>memories/protocols/bancho-stable.md</c> §8): string sender + string text +
/// string target + i32 sender_id. For client→server messages the sender fields are implicit
/// (the server fills them from the session); for server→client the server populates all four.
/// </remarks>
/// <param name="Sender">Display name of the sender.</param>
/// <param name="Text">Message body.</param>
/// <param name="Target">Destination channel name (e.g. <c>#osu</c>) or recipient username.</param>
/// <param name="SenderId">Sender's osu! user id.</param>
public readonly record struct BanchoMessage(string Sender, string Text, string Target, int SenderId);
