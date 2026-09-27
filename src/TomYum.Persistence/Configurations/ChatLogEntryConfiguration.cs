using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TomYum.Domain.Entities;

namespace TomYum.Persistence.Configurations;

/// <summary>Maps <see cref="ChatLogEntry"/>; must match <c>SchemaGuard</c>'s <c>chat_messages</c> DDL.</summary>
public sealed class ChatLogEntryConfiguration : IEntityTypeConfiguration<ChatLogEntry>
{
    public void Configure(EntityTypeBuilder<ChatLogEntry> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("chat_messages");
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedOnAdd();
        builder.Property(m => m.SentAt).IsRequired();
        builder.Property(m => m.SenderName).IsRequired().HasMaxLength(32);
        builder.Property(m => m.Target).IsRequired().HasMaxLength(64);
        builder.Property(m => m.Text).IsRequired().HasMaxLength(2048);
        builder.HasIndex(m => m.SentAt);
        builder.HasIndex(m => new { m.SenderId, m.Id });
        builder.HasIndex(m => new { m.RecipientId, m.Id });
        builder.HasIndex(m => new { m.Target, m.Id });
    }
}
