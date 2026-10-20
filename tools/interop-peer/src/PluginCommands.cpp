#include "PluginCommands.h"

#include "MessageFilter.h"
#include "RakMemoryOverride.h"
#include "RakPeerInterface.h"
#include "ReadyEvent.h"
#include "TwoWayAuthentication.h"

#include <cerrno>
#include <cstdlib>
#include <limits>

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

        int IntField(const json& command, const char* field, long long minimum, long long maximum, const long long* fallback)
        {
            long long value = fallback != nullptr ? Optional<long long>(command, field, *fallback) : Required<long long>(command, field);
            if (value < minimum || value > maximum)
                throw PluginCommandError(std::string("field '") + field + "' is out of range");
            return static_cast<int>(value);
        }

        int FilterSetField(const json& command, long long minimum)
        {
            return IntField(command, "filterSetId", minimum, std::numeric_limits<int>::max(), nullptr);
        }

        RakNet::TimeMS MillisecondsField(const json& command, const char* field)
        {
            const long long zero = 0;
            return static_cast<RakNet::TimeMS>(IntField(command, field, 0, std::numeric_limits<int>::max(), &zero));
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

        // An optional GUID; omitted means UNASSIGNED_RAKNET_GUID.
        RakNet::RakNetGUID OptionalGuidField(const json& command, const char* field)
        {
            auto it = command.find(field);
            if (it == command.end() || it->is_null())
                return RakNet::UNASSIGNED_RAKNET_GUID;
            return GuidField(command, field);
        }

        int EventIdField(const json& command)
        {
            return IntField(command, "eventId", std::numeric_limits<int>::min(), std::numeric_limits<int>::max(), nullptr);
        }

        const char* ReadyStatusName(RakNet::ReadyEventSystemStatus status)
        {
            switch (status)
            {
            case RakNet::RES_NOT_WAITING: return "RES_NOT_WAITING";
            case RakNet::RES_WAITING: return "RES_WAITING";
            case RakNet::RES_READY: return "RES_READY";
            case RakNet::RES_ALL_READY: return "RES_ALL_READY";
            case RakNet::RES_UNKNOWN_EVENT: return "RES_UNKNOWN_EVENT";
            }
            return "RES_UNKNOWN_EVENT";
        }

        std::string AddressText(const RakNet::SystemAddress& address)
        {
            if (address == RakNet::UNASSIGNED_SYSTEM_ADDRESS)
                return "";
            char buffer[128];
            address.ToString(true, buffer, ':');
            return buffer;
        }

        PluginCommands::FilterEvent Describe(const RakNet::AddressOrGUID& system, int filterSetId, int messageId)
        {
            PluginCommands::FilterEvent event;
            event.guid = system.rakNetGuid == RakNet::UNASSIGNED_RAKNET_GUID ? 0 : system.rakNetGuid.g;
            event.address = AddressText(system.systemAddress);
            event.filterSetId = filterSetId;
            event.messageId = messageId;
            return event;
        }

        void OnDisallowed(RakNet::RakPeerInterface*, RakNet::AddressOrGUID system, int filterSetId, void* userData, unsigned char messageId)
        {
            static_cast<std::vector<PluginCommands::FilterEvent>*>(userData)->push_back(Describe(system, filterSetId, messageId));
        }

        void OnTimeout(RakNet::RakPeerInterface*, RakNet::AddressOrGUID system, int filterSetId, void* userData)
        {
            static_cast<std::vector<PluginCommands::FilterEvent>*>(userData)->push_back(Describe(system, filterSetId, -1));
        }

        ordered_json ToJson(const std::vector<PluginCommands::FilterEvent>& events, bool withMessageId)
        {
            ordered_json array = ordered_json::array();
            for (const PluginCommands::FilterEvent& event : events)
            {
                ordered_json item;
                item["guid"] = std::to_string(event.guid);
                item["address"] = event.address;
                item["filterSetId"] = event.filterSetId;
                if (withMessageId)
                    item["messageId"] = event.messageId;
                array.push_back(item);
            }
            return array;
        }
    }

    PluginCommands::PluginCommands(RakNet::RakPeerInterface* peer) : peer_(peer) {}

    PluginCommands::~PluginCommands()
    {
        // The peer is destroyed first; it never frees plugins, so they are freed here.
        if (messageFilter_ != nullptr)
            RakNet::MessageFilter::DestroyInstance(messageFilter_);
        if (readyEvent_ != nullptr)
            RakNet::ReadyEvent::DestroyInstance(readyEvent_);
        if (twoWayAuthentication_ != nullptr)
            RakNet::OP_DELETE(twoWayAuthentication_, _FILE_AND_LINE_);
    }

    bool PluginCommands::Dispatch(const std::string& cmd, const json& command, ordered_json& result)
    {
        if (cmd == "messageFilterAttach")
        {
            if (messageFilter_ == nullptr)
            {
                messageFilter_ = RakNet::MessageFilter::GetInstance();
                // MessageFilter's constructor leaves this unset; -1 is its documented default.
                messageFilter_->SetAutoAddNewConnectionsToFilter(-1);
                peer_->AttachPlugin(messageFilter_);
            }
        }
        else if (cmd == "messageFilterSetAutoAdd")
        {
            RequireMessageFilter().SetAutoAddNewConnectionsToFilter(FilterSetField(command, -1));
        }
        else if (cmd == "messageFilterAllow")
        {
            int filterSetId = FilterSetField(command, 0);
            int start = IntField(command, "start", 0, 255, nullptr);
            int end = IntField(command, "end", start, 255, nullptr);
            RequireMessageFilter().SetAllowMessageID(Optional<bool>(command, "allow", true), start, end, filterSetId);
            TrackFilterSet(filterSetId);
        }
        else if (cmd == "messageFilterAllowRpc4")
        {
            int filterSetId = FilterSetField(command, 0);
            std::string uniqueId = Required<std::string>(command, "uniqueId");
            RequireMessageFilter().SetAllowRPC4(Optional<bool>(command, "allow", true), uniqueId.c_str(), filterSetId);
            TrackFilterSet(filterSetId);
        }
        else if (cmd == "messageFilterSetActions")
        {
            int filterSetId = FilterSetField(command, 0);
            RequireMessageFilter().SetActionOnDisallowedMessage(
                Optional<bool>(command, "kick", false), Optional<bool>(command, "ban", false), MillisecondsField(command, "banMs"), filterSetId);
            TrackFilterSet(filterSetId);
        }
        else if (cmd == "messageFilterSetMaxTime")
        {
            int filterSetId = FilterSetField(command, 0);
            RequireMessageFilter().SetFilterMaxTime(
                static_cast<int>(MillisecondsField(command, "ms")), Optional<bool>(command, "ban", false), MillisecondsField(command, "banMs"), filterSetId);
            TrackFilterSet(filterSetId);
        }
        else if (cmd == "messageFilterSetSystem")
        {
            int filterSetId = FilterSetField(command, -1);
            RequireMessageFilter().SetSystemFilterSet(RakNet::AddressOrGUID(GuidField(command, "target")), filterSetId);
            if (filterSetId >= 0)
                TrackFilterSet(filterSetId);
        }
        else if (cmd == "messageFilterGetSystem")
        {
            result["filterSetId"] = RequireMessageFilter().GetSystemFilterSet(RakNet::AddressOrGUID(GuidField(command, "target")));
        }
        else if (cmd == "messageFilterDeleteSet")
        {
            RequireMessageFilter().DeleteFilterSet(FilterSetField(command, 0));
        }
        else if (cmd == "messageFilterInfo")
        {
            MessageFilterInfo(result);
        }
        else
        {
            return DispatchReadyEvent(cmd, command, result) || DispatchTwoWayAuthentication(cmd, command, result);
        }
        return true;
    }

    bool PluginCommands::DispatchReadyEvent(const std::string& cmd, const json& command, ordered_json& result)
    {
        if (cmd == "readyEventAttach")
        {
            if (readyEvent_ == nullptr)
            {
                readyEvent_ = RakNet::ReadyEvent::GetInstance();
                readyEvent_->SetSendChannel(static_cast<unsigned char>(Optional<int>(command, "channel", 0)));
                peer_->AttachPlugin(readyEvent_);
            }
        }
        else if (cmd == "readyEventSet")
        {
            result["result"] = RequireReadyEvent().SetEvent(EventIdField(command), Optional<bool>(command, "isReady", true));
        }
        else if (cmd == "readyEventForceCompletion")
        {
            RequireReadyEvent().ForceCompletion(EventIdField(command));
        }
        else if (cmd == "readyEventDelete")
        {
            result["result"] = RequireReadyEvent().DeleteEvent(EventIdField(command));
        }
        else if (cmd == "readyEventAddToWaitList")
        {
            result["result"] = RequireReadyEvent().AddToWaitList(EventIdField(command), OptionalGuidField(command, "target"));
        }
        else if (cmd == "readyEventRemoveFromWaitList")
        {
            result["result"] = RequireReadyEvent().RemoveFromWaitList(EventIdField(command), OptionalGuidField(command, "target"));
        }
        else if (cmd == "readyEventInfo")
        {
            ReadyEventInfo(command, result);
        }
        else
        {
            return false;
        }
        return true;
    }

    RakNet::ReadyEvent& PluginCommands::RequireReadyEvent()
    {
        if (readyEvent_ == nullptr)
            throw PluginCommandError("no ReadyEvent is attached; send readyEventAttach first");
        return *readyEvent_;
    }

    void PluginCommands::ReadyEventInfo(const json& command, ordered_json& result)
    {
        RakNet::ReadyEvent& readyEvent = RequireReadyEvent();
        int eventId = EventIdField(command);
        ordered_json eventIds = ordered_json::array();
        for (unsigned i = 0; i < readyEvent.GetEventListSize(); ++i)
            eventIds.push_back(readyEvent.GetEventAtIndex(i));
        result["eventIds"] = eventIds;
        result["hasEvent"] = readyEvent.HasEvent(eventId);
        result["isSet"] = readyEvent.IsEventSet(eventId);
        result["isCompleted"] = readyEvent.IsEventCompleted(eventId);
        result["isCompletionProcessing"] = readyEvent.IsEventCompletionProcessing(eventId);
        ordered_json waitList = ordered_json::array();
        for (unsigned i = 0; i < readyEvent.GetRemoteWaitListSize(eventId); ++i)
        {
            RakNet::RakNetGUID guid = readyEvent.GetFromWaitListAtIndex(eventId, i);
            ordered_json item;
            item["guid"] = std::to_string(guid.g);
            item["status"] = ReadyStatusName(readyEvent.GetReadyStatus(eventId, guid));
            waitList.push_back(item);
        }
        result["waitList"] = waitList;
    }

    bool PluginCommands::DispatchTwoWayAuthentication(const std::string& cmd, const json& command, ordered_json& result)
    {
        if (cmd == "twoWayAuthAttach")
        {
            if (twoWayAuthentication_ == nullptr)
            {
                // TwoWayAuthentication.cpp has no STATIC_FACTORY_DEFINITIONS, so GetInstance is never defined.
                twoWayAuthentication_ = RakNet::OP_NEW<RakNet::TwoWayAuthentication>(_FILE_AND_LINE_);
                peer_->AttachPlugin(twoWayAuthentication_);
            }
        }
        else if (cmd == "twoWayAuthAddPassword")
        {
            std::string identifier = Required<std::string>(command, "identifier");
            std::string password = Required<std::string>(command, "password");
            result["result"] = RequireTwoWayAuthentication().AddPassword(identifier.c_str(), password.c_str());
        }
        else if (cmd == "twoWayAuthChallenge")
        {
            std::string identifier = Required<std::string>(command, "identifier");
            result["result"] = RequireTwoWayAuthentication().Challenge(identifier.c_str(), RakNet::AddressOrGUID(GuidField(command, "target")));
        }
        else if (cmd == "twoWayAuthClear")
        {
            RequireTwoWayAuthentication().Clear();
        }
        else
        {
            return false;
        }
        return true;
    }

    RakNet::TwoWayAuthentication& PluginCommands::RequireTwoWayAuthentication()
    {
        if (twoWayAuthentication_ == nullptr)
            throw PluginCommandError("no TwoWayAuthentication is attached; send twoWayAuthAttach first");
        return *twoWayAuthentication_;
    }

    RakNet::MessageFilter& PluginCommands::RequireMessageFilter()
    {
        if (messageFilter_ == nullptr)
            throw PluginCommandError("no MessageFilter is attached; send messageFilterAttach first");
        return *messageFilter_;
    }

    // Every filter set a command configures reports its disallowed messages and timeouts to messageFilterInfo.
    void PluginCommands::TrackFilterSet(int filterSetId)
    {
        messageFilter_->SetDisallowedMessageCallback(filterSetId, &disallowed_, &OnDisallowed);
        messageFilter_->SetTimeoutCallback(filterSetId, &timeouts_, &OnTimeout);
    }

    void PluginCommands::MessageFilterInfo(ordered_json& result)
    {
        RakNet::MessageFilter& filter = RequireMessageFilter();
        ordered_json filterSets = ordered_json::array();
        for (unsigned i = 0; i < filter.GetFilterSetCount(); ++i)
            filterSets.push_back(filter.GetFilterSetIDByIndex(i));
        result["filterSetIds"] = filterSets;
        result["systemCount"] = filter.GetSystemCount(-1);
        result["disallowed"] = ToJson(disallowed_, true);
        result["timeouts"] = ToJson(timeouts_, false);
    }
}
