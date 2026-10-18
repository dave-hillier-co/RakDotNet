namespace RakDotNet.Interop.Tests.Harness;

/// <summary>The peer replied <c>ok:false</c> to a command.</summary>
public sealed class PeerCommandException : Exception
{
    public PeerCommandException()
    {
        Command = "";
    }

    public PeerCommandException(string message) : base(message)
    {
        Command = "";
    }

    public PeerCommandException(string message, Exception innerException) : base(message, innerException)
    {
        Command = "";
    }

    public PeerCommandException(string command, string error) : base($"Peer command '{command}' failed: {error}")
    {
        Command = command;
    }

    public string Command { get; }
}

/// <summary>The peer process exited while a command was pending, before it was ready, or before a command was sent.</summary>
public sealed class PeerProcessExitedException : Exception
{
    public PeerProcessExitedException()
    {
    }

    public PeerProcessExitedException(string message) : base(message)
    {
    }

    public PeerProcessExitedException(string message, Exception innerException) : base(message, innerException)
    {
    }

    public PeerProcessExitedException(int? exitCode, string context, string standardError)
        : base($"Peer process exited (code {exitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}) {context}.{Environment.NewLine}stderr:{Environment.NewLine}{standardError}")
    {
        ExitCode = exitCode;
    }

    public int? ExitCode { get; }
}
