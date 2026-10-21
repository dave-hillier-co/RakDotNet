#include "FileCommands.h"

#include "Hex.h"
#include "Names.h"
#include "PluginCommands.h"

#include "DirectoryDeltaTransfer.h"
#include "FileList.h"
#include "FileListTransfer.h"
#include "FileListTransferCBInterface.h"
#include "FileOperations.h"
#include "IncrementalReadInterface.h"
#include "RakPeerInterface.h"
#include "SuperFastHash.h"

#include <cerrno>
#include <cstdint>
#include <cstdlib>
#include <limits>

using nlohmann::json;
using nlohmann::ordered_json;

namespace interop
{
    namespace
    {
        // Files up to this size are reported with their bytes as well as their hash.
        const unsigned int MaxReportedDataBytes = 1024;

        template <typename T>
        T Optional(const json& command, const char* field, T fallback)
        {
            auto it = command.find(field);
            if (it == command.end() || it->is_null())
                return fallback;
            return it->get<T>();
        }

        template <typename T>
        T Required(const json& command, const char* field)
        {
            auto it = command.find(field);
            if (it == command.end() || it->is_null())
                throw PluginCommandError(std::string("missing field '") + field + "'");
            return it->get<T>();
        }

        long long IntField(const json& command, const char* field, long long minimum, long long maximum, long long fallback)
        {
            long long value = Optional<long long>(command, field, fallback);
            if (value < minimum || value > maximum)
                throw PluginCommandError(std::string("field '") + field + "' is out of range");
            return value;
        }

        RakNet::RakNetGUID GuidField(const json& command, const char* field)
        {
            const json value = Required<json>(command, field);
            if (value.is_number_unsigned())
                return RakNet::RakNetGUID(value.get<std::uint64_t>());
            if (!value.is_string())
                throw PluginCommandError(std::string("field '") + field + "' must be a decimal GUID string");
            const std::string text = value.get<std::string>();
            if (text.empty() || text.find_first_not_of("0123456789") != std::string::npos)
                throw PluginCommandError(std::string("field '") + field + "' must be a decimal GUID string");
            errno = 0;
            unsigned long long g = std::strtoull(text.c_str(), nullptr, 10);
            if (errno == ERANGE)
                throw PluginCommandError(std::string("field '") + field + "' is out of range");
            return RakNet::RakNetGUID(static_cast<std::uint64_t>(g));
        }

        PacketPriority PriorityField(const json& command)
        {
            PacketPriority priority;
            if (!TryParsePriority(Optional<std::string>(command, "priority", "HIGH_PRIORITY"), priority))
                throw PluginCommandError("field 'priority' is not a PacketPriority name");
            return priority;
        }

        FileListNodeContext ContextField(const json& file)
        {
            auto it = file.find("context");
            if (it == file.end() || it->is_null())
                return FileListNodeContext(0, 0, 0, 0);
            return FileListNodeContext(
                static_cast<unsigned char>(IntField(*it, "op", 0, 255, 0)),
                static_cast<uint32_t>(IntField(*it, "extraData1", 0, 0xffffffffLL, 0)),
                static_cast<uint32_t>(IntField(*it, "extraData2", 0, 0xffffffffLL, 0)),
                static_cast<uint32_t>(IntField(*it, "extraData3", 0, 0xffffffffLL, 0)));
        }

        ordered_json ContextJson(const FileListNodeContext& context)
        {
            ordered_json result;
            result["op"] = context.op;
            result["extraData1"] = context.flnc_extraData1;
            result["extraData2"] = context.flnc_extraData2;
            result["extraData3"] = context.flnc_extraData3;
            return result;
        }

        ordered_json FileJson(const RakNet::FileListTransferCBInterface::OnFileStruct& file)
        {
            ordered_json result;
            result["setId"] = file.setID;
            result["fileIndex"] = file.fileIndex;
            result["fileName"] = file.fileName;
            result["byteLengthOfThisFile"] = file.byteLengthOfThisFile;
            result["bytesDownloadedForThisFile"] = file.bytesDownloadedForThisFile;
            result["numberOfFilesInThisSet"] = file.numberOfFilesInThisSet;
            result["byteLengthOfThisSet"] = file.byteLengthOfThisSet;
            result["bytesDownloadedForThisSet"] = file.bytesDownloadedForThisSet;
            result["context"] = ContextJson(file.context);
            result["senderGuid"] = std::to_string(file.senderGuid.g);
            return result;
        }

        // FileListTransferCBInterface recording what it is given. OnFile returns true so RakNet frees the data.
        class RecordingHandler : public RakNet::FileListTransferCBInterface
        {
        public:
            RecordingHandler(FileCommands::Records& records, bool allocateIriDataChunk)
                : records_(records), allocateIriDataChunk_(allocateIriDataChunk) {}

            void SetId(int setId) { setId_ = setId; }

            bool OnFile(OnFileStruct* onFileStruct) override
            {
                ordered_json record = FileJson(*onFileStruct);
                const bool hasData = onFileStruct->fileData != nullptr;
                record["hasData"] = hasData;
                if (hasData)
                {
                    record["hash"] = SuperFastHash(onFileStruct->fileData, static_cast<int>(onFileStruct->byteLengthOfThisFile));
                    if (onFileStruct->byteLengthOfThisFile <= MaxReportedDataBytes)
                        record["data"] = ToHex(reinterpret_cast<const unsigned char*>(onFileStruct->fileData), onFileStruct->byteLengthOfThisFile);
                }
                std::lock_guard<std::mutex> lock(records_.mutex);
                records_.files.push_back(record);
                return true;
            }

            void OnFileProgress(FileProgressStruct* fps) override
            {
                ordered_json record = FileJson(*fps->onFileStruct);
                record["partCount"] = fps->partCount;
                record["partTotal"] = fps->partTotal;
                record["dataChunkLength"] = fps->dataChunkLength;
                record["iriWriteOffset"] = fps->iriWriteOffset;
                record["hasIriDataChunk"] = fps->iriDataChunk != nullptr;
                if (fps->iriDataChunk != nullptr)
                    record["iriHash"] = SuperFastHash(fps->iriDataChunk, static_cast<int>(fps->dataChunkLength));
                fps->allocateIrIDataChunkAutomatically = allocateIriDataChunk_;
                std::lock_guard<std::mutex> lock(records_.mutex);
                records_.progress.push_back(record);
            }

            bool OnDownloadComplete(DownloadCompleteStruct* dcs) override
            {
                ordered_json record;
                record["setId"] = dcs->setID;
                record["numberOfFilesInThisSet"] = dcs->numberOfFilesInThisSet;
                record["byteLengthOfThisSet"] = dcs->byteLengthOfThisSet;
                record["senderGuid"] = std::to_string(dcs->senderGuid.g);
                std::lock_guard<std::mutex> lock(records_.mutex);
                records_.completed.push_back(record);
                return false;
            }

            void OnDereference() override
            {
                if (setId_ < 0)
                    return;
                std::lock_guard<std::mutex> lock(records_.mutex);
                records_.dereferenced.push_back(setId_);
            }

        private:
            FileCommands::Records& records_;
            bool allocateIriDataChunk_;
            int setId_ = -1;
        };

        // FileListProgress recording a sender's reports.
        class RecordingProgress : public RakNet::FileListProgress
        {
        public:
            explicit RecordingProgress(FileCommands::Records& records) : records_(records) {}

            void OnFilePush(const char* fileName, unsigned int fileLengthBytes, unsigned int offset, unsigned int bytesBeingSent, bool done,
                            RakNet::SystemAddress, unsigned short setId) override
            {
                ordered_json record;
                record["fileName"] = fileName;
                record["fileLengthBytes"] = fileLengthBytes;
                record["offset"] = offset;
                record["bytesBeingSent"] = bytesBeingSent;
                record["done"] = done;
                record["setId"] = setId;
                std::lock_guard<std::mutex> lock(records_.mutex);
                records_.pushes.push_back(record);
            }

            void OnFilePushesComplete(RakNet::SystemAddress, unsigned short setId) override
            {
                std::lock_guard<std::mutex> lock(records_.mutex);
                records_.pushesComplete.push_back(setId);
            }

            void OnSendAborted(RakNet::SystemAddress address) override
            {
                char text[64];
                address.ToString(true, text, ':');
                std::lock_guard<std::mutex> lock(records_.mutex);
                records_.sendAborted.push_back(std::string(text));
            }

        private:
            FileCommands::Records& records_;
        };
    }

    FileCommands::FileCommands(RakNet::RakPeerInterface* peer) : peer_(peer) {}

    FileCommands::~FileCommands()
    {
        // The peer is destroyed first; it never frees plugins, so they are freed here.
        if (directoryDeltaTransfer_ != nullptr)
            RakNet::DirectoryDeltaTransfer::DestroyInstance(directoryDeltaTransfer_);
        if (fileListTransfer_ != nullptr)
            RakNet::FileListTransfer::DestroyInstance(fileListTransfer_);
    }

    void FileCommands::AttachFileListTransfer()
    {
        if (fileListTransfer_ != nullptr)
            return;
        fileListTransfer_ = RakNet::FileListTransfer::GetInstance();
        sendProgress_.reset(new RecordingProgress(records_));
        fileListTransfer_->AddCallback(sendProgress_.get());
        peer_->AttachPlugin(fileListTransfer_);
    }

    bool FileCommands::Dispatch(const std::string& cmd, const json& command, ordered_json& result)
    {
        if (cmd == "fileListTransferAttach")
        {
            AttachFileListTransfer();
        }
        else if (cmd == "fileListTransferStartThreads")
        {
            RequireFileListTransfer().StartIncrementalReadThreads(static_cast<int>(IntField(command, "count", 1, 64, 1)));
        }
        else if (cmd == "fileListTransferSetupReceive")
        {
            RakNet::FileListTransfer& transfer = RequireFileListTransfer();
            RakNet::SystemAddress sender = peer_->GetSystemAddressFromGuid(GuidField(command, "sender"));
            RecordingHandler* handler = new RecordingHandler(records_, Optional<bool>(command, "allocateIriDataChunk", true));
            handlers_.push_back(std::unique_ptr<RakNet::FileListTransferCBInterface>(handler));
            unsigned short setId = transfer.SetupReceive(handler, false, sender);
            handler->SetId(setId == static_cast<unsigned short>(-1) ? -1 : setId);
            result["setId"] = setId;
        }
        else if (cmd == "fileListTransferSend")
        {
            Send(command);
        }
        else if (cmd == "fileListTransferCancelReceive")
        {
            RequireFileListTransfer().CancelReceive(static_cast<unsigned short>(IntField(command, "setId", 0, 65535, 0)));
        }
        else if (cmd == "fileListTransferInfo")
        {
            Info(command, result);
        }
        else if (cmd == "ddtAttach")
        {
            if (directoryDeltaTransfer_ == nullptr)
            {
                AttachFileListTransfer();
                directoryDeltaTransfer_ = RakNet::DirectoryDeltaTransfer::GetInstance();
                peer_->AttachPlugin(directoryDeltaTransfer_);
                directoryDeltaTransfer_->SetFileListTransferPlugin(fileListTransfer_);
            }
        }
        else if (cmd == "ddtSetApplicationDirectory")
        {
            RequireDirectoryDeltaTransfer().SetApplicationDirectory(Required<std::string>(command, "path").c_str());
        }
        else if (cmd == "ddtAddUploads")
        {
            RakNet::DirectoryDeltaTransfer& ddt = RequireDirectoryDeltaTransfer();
            ddt.AddUploadsFromSubdirectory(Required<std::string>(command, "subdir").c_str());
            result["filesForUpload"] = ddt.GetNumberOfFilesForUpload();
        }
        else if (cmd == "ddtClearUploads")
        {
            RequireDirectoryDeltaTransfer().ClearUploads();
        }
        else if (cmd == "ddtSetIncrementalRead")
        {
            RakNet::DirectoryDeltaTransfer& ddt = RequireDirectoryDeltaTransfer();
            unsigned int chunkSize = static_cast<unsigned int>(IntField(command, "chunkSize", 0, 0xffffffffLL, 0));
            if (chunkSize == 0)
            {
                ddt.SetDownloadRequestIncrementalReadInterface(nullptr, 0);
            }
            else
            {
                if (!incrementalRead_)
                    incrementalRead_.reset(new RakNet::IncrementalReadInterface());
                ddt.SetDownloadRequestIncrementalReadInterface(incrementalRead_.get(), chunkSize);
            }
        }
        else if (cmd == "ddtDownload")
        {
            RakNet::DirectoryDeltaTransfer& ddt = RequireDirectoryDeltaTransfer();
            RakNet::SystemAddress host = peer_->GetSystemAddressFromGuid(GuidField(command, "target"));
            if (host == RakNet::UNASSIGNED_SYSTEM_ADDRESS)
                throw PluginCommandError("no connection with the target guid");
            if (!downloadHandler_)
                downloadHandler_.reset(new RecordingHandler(records_, true));
            const std::string subdir = Required<std::string>(command, "subdir");
            const std::string outputSubdir = Required<std::string>(command, "outputSubdir");
            result["setId"] = ddt.DownloadFromSubdirectory(
                subdir.c_str(), outputSubdir.c_str(), Optional<bool>(command, "prependAppDir", true), host, downloadHandler_.get(),
                PriorityField(command), static_cast<char>(IntField(command, "channel", 0, 31, 0)), nullptr);
        }
        else
        {
            return false;
        }
        return true;
    }

    void FileCommands::Send(const json& command)
    {
        RakNet::FileListTransfer& transfer = RequireFileListTransfer();
        RakNet::SystemAddress target = peer_->GetSystemAddressFromGuid(GuidField(command, "target"));
        if (target == RakNet::UNASSIGNED_SYSTEM_ADDRESS)
            throw PluginCommandError("no connection with the target guid");
        unsigned short setId = static_cast<unsigned short>(IntField(command, "setId", 0, 65535, 0));

        RakNet::FileList list;
        for (const json& file : Required<json>(command, "files"))
        {
            const std::string name = Required<std::string>(file, "name");
            const std::string path = Optional<std::string>(file, "path", name);
            const FileListNodeContext context = ContextField(file);
            if (Optional<bool>(file, "reference", false))
            {
                // Samples/FileListTransfer: a reference with its length, read when sent.
                unsigned int length = static_cast<unsigned int>(IntField(file, "fileLength", 0, 0xffffffffLL, GetFileLength(path.c_str())));
                list.AddFile(name.c_str(), path.c_str(), 0, length, length, context, true);
            }
            else
            {
                std::vector<unsigned char> data;
                if (!TryFromHex(Optional<std::string>(file, "data", ""), data))
                    throw PluginCommandError("field 'data' is not hex");
                unsigned int length = static_cast<unsigned int>(data.size());
                unsigned int fileLength = static_cast<unsigned int>(IntField(file, "fileLength", 0, 0xffffffffLL, length));
                list.AddFile(name.c_str(), path.c_str(), data.empty() ? nullptr : reinterpret_cast<const char*>(data.data()), length, fileLength, context);
            }
        }

        RakNet::IncrementalReadInterface* reader = nullptr;
        if (Optional<bool>(command, "incremental", false))
        {
            if (!incrementalRead_)
                incrementalRead_.reset(new RakNet::IncrementalReadInterface());
            reader = incrementalRead_.get();
        }
        unsigned int chunkSize = static_cast<unsigned int>(IntField(command, "chunkSize", 1, 0xffffffffLL, 262144 * 4 * 16));
        transfer.Send(&list, peer_, target, setId, PriorityField(command), static_cast<char>(IntField(command, "channel", 0, 31, 0)), reader, chunkSize);
    }

    void FileCommands::Info(const json& command, ordered_json& result)
    {
        RakNet::FileListTransfer& transfer = RequireFileListTransfer();
        {
            std::lock_guard<std::mutex> lock(records_.mutex);
            result["files"] = records_.files;
            result["progress"] = records_.progress;
            result["completed"] = records_.completed;
            result["dereferenced"] = records_.dereferenced;
            result["pushes"] = records_.pushes;
            result["pushesComplete"] = records_.pushesComplete;
            result["sendAborted"] = records_.sendAborted;
        }
        if (command.contains("setId") && !command["setId"].is_null())
            result["handlerActive"] = transfer.IsHandlerActive(static_cast<unsigned short>(IntField(command, "setId", 0, 65535, 0)));
        if (command.contains("target") && !command["target"].is_null())
            result["pendingFiles"] = transfer.GetPendingFilesToAddress(peer_->GetSystemAddressFromGuid(GuidField(command, "target")));
    }

    RakNet::FileListTransfer& FileCommands::RequireFileListTransfer()
    {
        if (fileListTransfer_ == nullptr)
            throw PluginCommandError("no FileListTransfer is attached; send fileListTransferAttach first");
        return *fileListTransfer_;
    }

    RakNet::DirectoryDeltaTransfer& FileCommands::RequireDirectoryDeltaTransfer()
    {
        if (directoryDeltaTransfer_ == nullptr)
            throw PluginCommandError("no DirectoryDeltaTransfer is attached; send ddtAttach first");
        return *directoryDeltaTransfer_;
    }
}
