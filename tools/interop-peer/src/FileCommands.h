#pragma once

#include <nlohmann/json.hpp>

#include <memory>
#include <mutex>
#include <string>
#include <vector>

namespace RakNet
{
    class DirectoryDeltaTransfer;
    class FileListTransfer;
    class FileListTransferCBInterface;
    class FileListProgress;
    class IncrementalReadInterface;
    class RakPeerInterface;
}

namespace interop
{
    // The commands that attach and drive FileListTransfer and DirectoryDeltaTransfer on the peer, specified in
    // docs/interop-protocol.md. Commands run with the peer's lock held; the callbacks they record may also run on
    // FileListTransfer's incremental read threads, so the records have their own lock.
    class FileCommands
    {
    public:
        explicit FileCommands(RakNet::RakPeerInterface* peer);
        ~FileCommands();

        FileCommands(const FileCommands&) = delete;
        FileCommands& operator=(const FileCommands&) = delete;

        // Runs cmd when it is a file command and returns true; returns false for any other cmd.
        bool Dispatch(const std::string& cmd, const nlohmann::json& command, nlohmann::ordered_json& result);

        // What the handlers and progress callbacks reported, in the order they reported it.
        struct Records
        {
            std::mutex mutex;
            nlohmann::ordered_json files = nlohmann::ordered_json::array();
            nlohmann::ordered_json progress = nlohmann::ordered_json::array();
            nlohmann::ordered_json completed = nlohmann::ordered_json::array();
            nlohmann::ordered_json dereferenced = nlohmann::ordered_json::array();
            nlohmann::ordered_json pushes = nlohmann::ordered_json::array();
            nlohmann::ordered_json pushesComplete = nlohmann::ordered_json::array();
            nlohmann::ordered_json sendAborted = nlohmann::ordered_json::array();
        };

    private:
        RakNet::FileListTransfer& RequireFileListTransfer();
        RakNet::DirectoryDeltaTransfer& RequireDirectoryDeltaTransfer();
        void AttachFileListTransfer();
        void Send(const nlohmann::json& command);
        void Info(const nlohmann::json& command, nlohmann::ordered_json& result);

        RakNet::RakPeerInterface* peer_;
        RakNet::FileListTransfer* fileListTransfer_ = nullptr;
        RakNet::DirectoryDeltaTransfer* directoryDeltaTransfer_ = nullptr;
        std::unique_ptr<RakNet::IncrementalReadInterface> incrementalRead_;
        std::unique_ptr<RakNet::FileListProgress> sendProgress_;
        std::unique_ptr<RakNet::FileListTransferCBInterface> downloadHandler_;
        std::vector<std::unique_ptr<RakNet::FileListTransferCBInterface>> handlers_;
        Records records_;
    };
}
