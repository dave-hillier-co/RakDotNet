using System.Text.Json.Nodes;
using RakDotNet.Plugins;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Interop.Tests.Harness;

public sealed partial class NativePeerDriver : IFileTransferDriver
{
    public Task AttachFileListTransferAsync(CancellationToken cancellationToken = default) =>
        SendCommandAsync("fileListTransferAttach", [], cancellationToken);

    public Task StartFileListTransferThreadsAsync(int count, CancellationToken cancellationToken = default) =>
        SendCommandAsync("fileListTransferStartThreads", new JsonObject { ["count"] = count }, cancellationToken);

    public async Task<ushort> SetupFileReceiveAsync(ulong sender, bool allocateIriDataChunk = true, CancellationToken cancellationToken = default) =>
        (ushort)(await SendCommandAsync("fileListTransferSetupReceive", new JsonObject { ["sender"] = FormatGuid(sender), ["allocateIriDataChunk"] = allocateIriDataChunk }, cancellationToken).ConfigureAwait(false))["setId"]!;

    public Task SendFilesAsync(ulong target, ushort setId, IReadOnlyList<FileToSend> files, Priority priority = Priority.High, byte channel = 0,
                               bool incremental = false, uint? chunkSize = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        var array = new JsonArray();
        foreach (var file in files)
        {
            var item = new JsonObject
            {
                ["name"] = file.Name,
                ["reference"] = file.Reference,
                ["context"] = new JsonObject
                {
                    ["op"] = file.Context.Op,
                    ["extraData1"] = file.Context.ExtraData1,
                    ["extraData2"] = file.Context.ExtraData2,
                    ["extraData3"] = file.Context.ExtraData3,
                },
            };
            if (!file.Data.IsEmpty)
                item["data"] = Convert.ToHexStringLower(file.Data.Span);
            if (file.Path is not null)
                item["path"] = file.Path;
            if (file.FileLength is { } length)
                item["fileLength"] = length;
            array.Add(item);
        }
        var fields = new JsonObject
        {
            ["target"] = FormatGuid(target),
            ["setId"] = setId,
            ["files"] = array,
            ["priority"] = ToProtocolName(priority),
            ["channel"] = channel,
            ["incremental"] = incremental,
        };
        if (chunkSize is { } chunk)
            fields["chunkSize"] = chunk;
        return SendCommandAsync("fileListTransferSend", fields, cancellationToken);
    }

    public Task CancelFileReceiveAsync(ushort setId, CancellationToken cancellationToken = default) =>
        SendCommandAsync("fileListTransferCancelReceive", new JsonObject { ["setId"] = setId }, cancellationToken);

    public async Task<FileTransferInfo> GetFileTransferInfoAsync(ushort? setId = null, ulong? target = null, CancellationToken cancellationToken = default)
    {
        var fields = new JsonObject();
        if (setId is { } id)
            fields["setId"] = id;
        if (target is { } guid)
            fields["target"] = FormatGuid(guid);
        var reply = await SendCommandAsync("fileListTransferInfo", fields, cancellationToken).ConfigureAwait(false);
        return new FileTransferInfo(
            [.. reply["files"]!.AsArray().Select(n => ParseFile(n!.AsObject()))],
            [.. reply["progress"]!.AsArray().Select(n => ParseProgress(n!.AsObject()))],
            [.. reply["completed"]!.AsArray().Select(n => new CompletedRecord((ushort)n!["setId"]!, (uint)n["numberOfFilesInThisSet"]!, (uint)n["byteLengthOfThisSet"]!, ParseGuid(n["senderGuid"])))],
            [.. reply["dereferenced"]!.AsArray().Select(n => (ushort)n!)],
            [.. reply["pushes"]!.AsArray().Select(n => new PushRecord((string)n!["fileName"]!, (uint)n["fileLengthBytes"]!, (uint)n["offset"]!, (uint)n["bytesBeingSent"]!, (bool)n["done"]!, (ushort)n["setId"]!))],
            [.. reply["pushesComplete"]!.AsArray().Select(n => (ushort)n!)],
            [.. reply["sendAborted"]!.AsArray().Select(n => (string)n!)],
            reply["handlerActive"] is { } active ? (bool)active : null,
            reply["pendingFiles"] is { } pending ? (uint)pending : null);
    }

    public Task AttachDirectoryDeltaTransferAsync(CancellationToken cancellationToken = default) =>
        SendCommandAsync("ddtAttach", [], cancellationToken);

    public Task SetDdtApplicationDirectoryAsync(string path, CancellationToken cancellationToken = default) =>
        SendCommandAsync("ddtSetApplicationDirectory", new JsonObject { ["path"] = path }, cancellationToken);

    public async Task<int> AddDdtUploadsAsync(string subdirectory, CancellationToken cancellationToken = default) =>
        (int)(await SendCommandAsync("ddtAddUploads", new JsonObject { ["subdir"] = subdirectory }, cancellationToken).ConfigureAwait(false))["filesForUpload"]!;

    public Task ClearDdtUploadsAsync(CancellationToken cancellationToken = default) =>
        SendCommandAsync("ddtClearUploads", [], cancellationToken);

    public Task SetDdtIncrementalReadAsync(uint chunkSize, CancellationToken cancellationToken = default) =>
        SendCommandAsync("ddtSetIncrementalRead", new JsonObject { ["chunkSize"] = chunkSize }, cancellationToken);

    public async Task<ushort> DdtDownloadAsync(string subdirectory, string outputSubdirectory, bool prependAppDir, ulong target, Priority priority = Priority.High,
                                               byte channel = 0, CancellationToken cancellationToken = default) =>
        (ushort)(await SendCommandAsync("ddtDownload", new JsonObject
        {
            ["subdir"] = subdirectory,
            ["outputSubdir"] = outputSubdirectory,
            ["prependAppDir"] = prependAppDir,
            ["target"] = FormatGuid(target),
            ["priority"] = ToProtocolName(priority),
            ["channel"] = channel,
        }, cancellationToken).ConfigureAwait(false))["setId"]!;

    private static FileRecord ParseFile(JsonObject n) => new(
        (ushort)n["setId"]!,
        (uint)n["fileIndex"]!,
        (string)n["fileName"]!,
        (uint)n["byteLengthOfThisFile"]!,
        (uint)n["bytesDownloadedForThisFile"]!,
        (uint)n["numberOfFilesInThisSet"]!,
        (uint)n["byteLengthOfThisSet"]!,
        (uint)n["bytesDownloadedForThisSet"]!,
        ParseContext(n["context"]!.AsObject()),
        ParseGuid(n["senderGuid"]),
        (bool)n["hasData"]!,
        n["hash"] is { } hash ? (uint)hash : null,
        n["data"] is { } data ? Convert.FromHexString((string)data!) : null);

    private static ProgressRecord ParseProgress(JsonObject n) => new(
        (ushort)n["setId"]!,
        (uint)n["fileIndex"]!,
        (string)n["fileName"]!,
        (uint)n["partCount"]!,
        (uint)n["partTotal"]!,
        (uint)n["dataChunkLength"]!,
        (uint)n["iriWriteOffset"]!,
        (uint)n["bytesDownloadedForThisFile"]!,
        (uint)n["bytesDownloadedForThisSet"]!,
        (bool)n["hasIriDataChunk"]!,
        n["iriHash"] is { } hash ? (uint)hash : null);

    private static FileListNodeContext ParseContext(JsonObject n) =>
        new((byte)n["op"]!, (uint)n["extraData1"]!, (uint)n["extraData2"]!, (uint)n["extraData3"]!);
}
