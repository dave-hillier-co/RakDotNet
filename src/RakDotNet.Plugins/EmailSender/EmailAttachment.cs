namespace RakDotNet.Plugins;

/// <summary>
/// A file attached to an email: the FileList entry <see cref="EmailSender.SendAsync"/> reads, its filename and data
/// (EmailSender.cpp:255-266).
/// </summary>
public sealed record EmailAttachment(string FileName, ReadOnlyMemory<byte> Data);
