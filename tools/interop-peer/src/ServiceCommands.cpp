#include "ServiceCommands.h"

#include "Hex.h"
#include "PluginCommands.h"
#include "RedirectedServices.h"
#include "ServiceRedirects.h"

#include "EmailSender.h"
#include "FileList.h"
#include "TCPInterface.h"

#include <cstring>
#include <limits>
#include <unistd.h>
#include <vector>

using nlohmann::json;
using nlohmann::ordered_json;

namespace interop
{
    namespace
    {
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

        unsigned short Port(long long value, const char* field)
        {
            if (value < 0 || value > std::numeric_limits<unsigned short>::max())
                throw PluginCommandError(std::string("field '") + field + "' is out of range");
            return static_cast<unsigned short>(value);
        }

        // An optional string field: absent or null is a null pointer, as RakNet's optional arguments are.
        struct OptionalText
        {
            OptionalText(const json& command, const char* field)
            {
                auto it = command.find(field);
                present = it != command.end() && !it->is_null();
                if (present)
                    text = it->get<std::string>();
            }

            const char* Get() const { return present ? text.c_str() : nullptr; }

            bool present;
            std::string text;
        };

        // The redirects field: [{"host", "toHost", "toPort"}].
        void ApplyRedirects(const json& command)
        {
            json redirects = Optional<json>(command, "redirects", json::array());
            if (!redirects.is_array())
                throw PluginCommandError("field 'redirects' must be an array");
            for (const json& redirect : redirects)
            {
                SetServiceRedirect(Required<std::string>(redirect, "host"), Required<std::string>(redirect, "toHost"),
                    Port(Required<long long>(redirect, "toPort"), "toPort"));
            }
        }

        // TCPInterface::Stop closes the socket of every slot with none, and that socket is 0 (TCPInterface.cpp:298-304,
        // 1266-1278): it closes stdin, which carries the protocol. EmailSender stops its interface at the end of Send and
        // DynDNS when it completes, so stdin is kept aside while they run and put back after.
        class StdinGuard
        {
        public:
            StdinGuard() : saved_(dup(STDIN_FILENO)) {}

            ~StdinGuard()
            {
                if (saved_ >= 0)
                {
                    dup2(saved_, STDIN_FILENO);
                    close(saved_);
                }
            }

            StdinGuard(const StdinGuard&) = delete;
            StdinGuard& operator=(const StdinGuard&) = delete;

        private:
            int saved_;
        };

        std::string AddressText(const RakNet::SystemAddress& address)
        {
            if (address == RakNet::UNASSIGNED_SYSTEM_ADDRESS)
                return "";
            char buffer[128];
            address.ToString(true, buffer, ':');
            return buffer;
        }
    }

    // Samples/RackspaceConsole's DisplayHTMLPage: every result arrives at ExecuteDefault, and is kept until rackspacePoll.
    class RackspaceRecorder : public RakNet::RackspaceEventCallback_Default
    {
    public:
        void ExecuteDefault(const char* callbackName, RakNet::RackspaceEventType eventType, const char* htmlAdditionalInfo) override
        {
            ordered_json entry;
            entry["callback"] = callbackName;
            entry["eventType"] = static_cast<int>(eventType);
            entry["eventName"] = RakNet::InteropRackspace::EventTypeToString(eventType);
            entry["html"] = ToHex(reinterpret_cast<const unsigned char*>(htmlAdditionalInfo), std::strlen(htmlAdditionalInfo));
            results.push_back(entry);
        }

        void OnConnectionAttemptFailure(RakNet::RackspaceOperationType operationType, const char* url) override
        {
            ordered_json entry;
            entry["operationType"] = static_cast<int>(operationType);
            entry["url"] = url;
            failures.push_back(entry);
        }

        ordered_json results = ordered_json::array();
        ordered_json failures = ordered_json::array();
    };

    ServiceCommands::ServiceCommands() = default;

    ServiceCommands::~ServiceCommands()
    {
        delete dynDns_;
        delete rackspace_;
        // Deleting the interface stops it, which closes descriptor 0 (TCPInterface.cpp:298-304); the process is ending.
        delete rackspaceTcp_;
    }

    bool ServiceCommands::Dispatch(const std::string& cmd, const json& command, ordered_json& result)
    {
        if (cmd == "emailSend")
            EmailSend(command, result);
        else if (cmd == "dynDnsStart")
            DynDnsStart(command);
        else if (cmd == "dynDnsUpdate")
            DynDnsUpdate(result);
        else if (cmd == "rackspaceAuthenticate")
            RackspaceAuthenticate(command, result);
        else if (cmd == "rackspaceOperation")
            RackspaceOperation(command);
        else if (cmd == "rackspacePoll")
            RackspacePoll(result);
        else
            return false;
        return true;
    }

    // Samples/SendEmail/main.cpp:58-77, with the arguments given and doPrintf false: RakNet's debug printf would write to
    // stdout, which carries the protocol. Send blocks until it is done, and so does this command.
    void ServiceCommands::EmailSend(const json& command, ordered_json& result)
    {
        std::string host = Required<std::string>(command, "host");
        unsigned short port = Port(Required<long long>(command, "port"), "port");
        std::string body = Required<std::string>(command, "body");
        OptionalText sender(command, "sender");
        OptionalText recipient(command, "recipient");
        OptionalText senderName(command, "senderName");
        OptionalText recipientName(command, "recipientName");
        OptionalText subject(command, "subject");
        OptionalText password(command, "password");

        RakNet::FileList files;
        json attachments = Optional<json>(command, "attachments", json::array());
        if (!attachments.is_array())
            throw PluginCommandError("field 'attachments' must be an array");
        for (const json& attachment : attachments)
        {
            std::string name = Required<std::string>(attachment, "name");
            std::vector<unsigned char> data;
            if (!TryFromHex(Required<std::string>(attachment, "data"), data))
                throw PluginCommandError("field 'data' is not valid hex");
            // Samples/SendEmail/main.cpp:56: the data is copied into the list.
            files.AddFile(name.c_str(), name.c_str(), reinterpret_cast<const char*>(data.data()), static_cast<unsigned>(data.size()),
                static_cast<unsigned>(data.size()), FileListNodeContext(0, 0, 0, 0), false);
        }

        RakNet::EmailSender emailSender;
        const char* outcome;
        {
            StdinGuard guard;
            outcome = emailSender.Send(host.c_str(), port, sender.Get(), recipient.Get(), senderName.Get(), recipientName.Get(),
                subject.Get(), body.c_str(), &files, false, password.Get());
        }
        if (outcome != nullptr)
            result["result"] = outcome;
        else
            result["result"] = nullptr;
    }

    // DynDNS::UpdateHostIPAsynch, with checkip.dyndns.org and members.dyndns.org redirected to the given servers.
    void ServiceCommands::DynDnsStart(const json& command)
    {
        std::string dnsHost = Required<std::string>(command, "dnsHost");
        OptionalText newIp(command, "newIp");
        std::string credentials = Required<std::string>(command, "usernameAndPassword");
        ApplyRedirects(command);
        if (dynDns_ == nullptr)
            dynDns_ = new RakNet::InteropDynDNS();
        {
            StdinGuard guard;
            dynDns_->UpdateHostIPAsynch(dnsHost.c_str(), newIp.Get(), credentials.c_str());
        }
        dynDnsStarted_ = true;
    }

    // One Update, then what DynDNS reports. The result and description are read only once an update has completed,
    // because they are uninitialised before.
    void ServiceCommands::DynDnsUpdate(ordered_json& result)
    {
        if (dynDns_ == nullptr || !dynDnsStarted_)
            throw PluginCommandError("no DynDNS update; send dynDnsStart first");
        {
            StdinGuard guard;
            dynDns_->Update();
        }
        result["running"] = dynDns_->IsRunning();
        result["completed"] = dynDns_->IsCompleted();
        if (dynDns_->IsCompleted())
        {
            result["resultCode"] = static_cast<int>(dynDns_->GetCompletedResultCode());
            result["description"] = dynDns_->GetCompletedDescription();
            result["successful"] = dynDns_->WasResultSuccessful();
        }
        result["myPublicIp"] = dynDns_->GetMyPublicIP();
    }

    // Samples/RackspaceConsole/main.cpp:213-226: a TCPInterface started with one connection, the callback, Authenticate.
    void ServiceCommands::RackspaceAuthenticate(const json& command, ordered_json& result)
    {
        std::string url = Required<std::string>(command, "url");
        std::string user = Required<std::string>(command, "user");
        std::string key = Required<std::string>(command, "key");
        ApplyRedirects(command);
        if (rackspace_ == nullptr)
        {
            long long maxConnections = Optional<long long>(command, "maxConnections", 1);
            if (maxConnections < 1 || maxConnections > std::numeric_limits<unsigned short>::max())
                throw PluginCommandError("field 'maxConnections' is out of range");
            rackspaceTcp_ = new RakNet::TCPInterface();
            rackspaceTcp_->Start(0, 0, static_cast<unsigned short>(maxConnections));
            rackspace_ = new RakNet::InteropRackspace();
            recorder_.reset(new RackspaceRecorder());
            rackspace_->AddEventCallback(recorder_.get());
        }
        result["address"] = AddressText(rackspace_->Authenticate(rackspaceTcp_, url.c_str(), user.c_str(), key.c_str()));
    }

    // Samples/RackspaceConsole/main.cpp:250-420: each command by name, with its arguments in order.
    void ServiceCommands::RackspaceOperation(const json& command)
    {
        if (rackspace_ == nullptr)
            throw PluginCommandError("no Rackspace; send rackspaceAuthenticate first");
        std::string op = Required<std::string>(command, "op");
        json args = Optional<json>(command, "args", json::array());
        if (!args.is_array())
            throw PluginCommandError("field 'args' must be an array of strings");
        auto arg = [&](std::size_t index) -> RakNet::RakString {
            if (index >= args.size() || !args[index].is_string())
                throw PluginCommandError("operation '" + op + "' needs more arguments");
            return RakNet::RakString(args[index].get<std::string>().c_str());
        };
        RakNet::InteropRackspace& r = *rackspace_;
        if (op == "ListServers") r.ListServers();
        else if (op == "ListServersWithDetails") r.ListServersWithDetails();
        else if (op == "CreateServer") r.CreateServer(arg(0), arg(1), arg(2));
        else if (op == "GetServerDetails") r.GetServerDetails(arg(0));
        else if (op == "UpdateServerNameOrPassword") r.UpdateServerNameOrPassword(arg(0), arg(1), arg(2));
        else if (op == "DeleteServer") r.DeleteServer(arg(0));
        else if (op == "ListServerAddresses") r.ListServerAddresses(arg(0));
        else if (op == "ShareServerAddress") r.ShareServerAddress(arg(0), arg(1));
        else if (op == "DeleteServerAddress") r.DeleteServerAddress(arg(0), arg(1));
        else if (op == "RebootServer") r.RebootServer(arg(0), arg(1));
        else if (op == "RebuildServer") r.RebuildServer(arg(0), arg(1));
        else if (op == "ResizeServer") r.ResizeServer(arg(0), arg(1));
        else if (op == "ConfirmResizedServer") r.ConfirmResizedServer(arg(0));
        else if (op == "RevertResizedServer") r.RevertResizedServer(arg(0));
        else if (op == "ListFlavors") r.ListFlavors();
        else if (op == "GetFlavorDetails") r.GetFlavorDetails(arg(0));
        else if (op == "ListImages") r.ListImages();
        else if (op == "CreateImage") r.CreateImage(arg(0), arg(1));
        else if (op == "GetImageDetails") r.GetImageDetails(arg(0));
        else if (op == "DeleteImage") r.DeleteImage(arg(0));
        else if (op == "ListSharedIPGroups") r.ListSharedIPGroups();
        else if (op == "ListSharedIPGroupsWithDetails") r.ListSharedIPGroupsWithDetails();
        else if (op == "CreateSharedIPGroup") r.CreateSharedIPGroup(arg(0), arg(1));
        else if (op == "GetSharedIPGroupDetails") r.GetSharedIPGroupDetails(arg(0));
        else if (op == "DeleteSharedIPGroup") r.DeleteSharedIPGroup(arg(0));
        else
            throw PluginCommandError("unknown operation '" + op + "'");
    }

    // One pass of Samples/RackspaceConsole's loop (main.cpp:233-240), then the callbacks it caused and those before.
    void ServiceCommands::RackspacePoll(ordered_json& result)
    {
        if (rackspace_ == nullptr)
            throw PluginCommandError("no Rackspace; send rackspaceAuthenticate first");
        for (RakNet::Packet* packet = rackspaceTcp_->Receive(); packet != nullptr; rackspaceTcp_->DeallocatePacket(packet), packet = rackspaceTcp_->Receive())
            rackspace_->OnReceive(packet);
        RakNet::SystemAddress lost = rackspaceTcp_->HasLostConnection();
        if (lost != RakNet::UNASSIGNED_SYSTEM_ADDRESS)
            rackspace_->OnClosedConnection(lost);
        result["results"] = recorder_->results;
        result["failures"] = recorder_->failures;
        recorder_->results = ordered_json::array();
        recorder_->failures = ordered_json::array();
    }
}
