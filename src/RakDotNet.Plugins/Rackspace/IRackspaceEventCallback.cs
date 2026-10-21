namespace RakDotNet.Plugins;

/// <summary>
/// Rackspace2EventCallback and RackspaceEventCallback_Default (Rackspace.h:89-158) in one interface: each result goes
/// to its method, which by default passes its name to <see cref="ExecuteDefault"/>. Implement the methods wanted.
/// </summary>
/// <remarks><c>htmlAdditionalInfo</c> is everything the server sent on the operation's connection.</remarks>
public interface IRackspaceEventCallback
{
    /// <summary>ExecuteDefault: what each result method does unless implemented; nothing by default.</summary>
    void ExecuteDefault(string callbackName, RackspaceEventType eventType, string htmlAdditionalInfo)
    {
    }

    /// <summary>The result of <see cref="Rackspace.Authenticate"/>.</summary>
    void OnAuthenticationResult(RackspaceEventType eventType, string htmlAdditionalInfo) => ExecuteDefault("OnAuthenticationResult", eventType, htmlAdditionalInfo);

    /// <summary>The result of <see cref="Rackspace.ListServers"/>.</summary>
    void OnListServersResult(RackspaceEventType eventType, string htmlAdditionalInfo) => ExecuteDefault("OnListServersResult", eventType, htmlAdditionalInfo);

    /// <summary>The result of <see cref="Rackspace.ListServersWithDetails"/>.</summary>
    void OnListServersWithDetailsResult(RackspaceEventType eventType, string htmlAdditionalInfo) => ExecuteDefault("OnListServersWithDetailsResult", eventType, htmlAdditionalInfo);

    /// <summary>The result of <see cref="Rackspace.CreateServer"/>.</summary>
    void OnCreateServerResult(RackspaceEventType eventType, string htmlAdditionalInfo) => ExecuteDefault("OnCreateServerResult", eventType, htmlAdditionalInfo);

    /// <summary>The result of <see cref="Rackspace.GetServerDetails"/>.</summary>
    void OnGetServerDetails(RackspaceEventType eventType, string htmlAdditionalInfo) => ExecuteDefault("OnGetServerDetails", eventType, htmlAdditionalInfo);

    /// <summary>The result of <see cref="Rackspace.UpdateServerNameOrPassword"/>.</summary>
    void OnUpdateServerNameOrPassword(RackspaceEventType eventType, string htmlAdditionalInfo) => ExecuteDefault("OnUpdateServerNameOrPassword", eventType, htmlAdditionalInfo);

    /// <summary>The result of <see cref="Rackspace.DeleteServer"/>.</summary>
    void OnDeleteServer(RackspaceEventType eventType, string htmlAdditionalInfo) => ExecuteDefault("OnDeleteServer", eventType, htmlAdditionalInfo);

    /// <summary>The result of <see cref="Rackspace.ListServerAddresses"/>.</summary>
    void OnListServerAddresses(RackspaceEventType eventType, string htmlAdditionalInfo) => ExecuteDefault("OnListServerAddresses", eventType, htmlAdditionalInfo);

    /// <summary>The result of <see cref="Rackspace.ShareServerAddress"/>.</summary>
    void OnShareServerAddress(RackspaceEventType eventType, string htmlAdditionalInfo) => ExecuteDefault("OnShareServerAddress", eventType, htmlAdditionalInfo);

    /// <summary>The result of <see cref="Rackspace.DeleteServerAddress"/>.</summary>
    void OnDeleteServerAddress(RackspaceEventType eventType, string htmlAdditionalInfo) => ExecuteDefault("OnDeleteServerAddress", eventType, htmlAdditionalInfo);

    /// <summary>The result of <see cref="Rackspace.RebootServer"/>.</summary>
    void OnRebootServer(RackspaceEventType eventType, string htmlAdditionalInfo) => ExecuteDefault("OnRebootServer", eventType, htmlAdditionalInfo);

    /// <summary>The result of <see cref="Rackspace.RebuildServer"/>.</summary>
    void OnRebuildServer(RackspaceEventType eventType, string htmlAdditionalInfo) => ExecuteDefault("OnRebuildServer", eventType, htmlAdditionalInfo);

    /// <summary>The result of <see cref="Rackspace.ResizeServer"/>.</summary>
    void OnResizeServer(RackspaceEventType eventType, string htmlAdditionalInfo) => ExecuteDefault("OnResizeServer", eventType, htmlAdditionalInfo);

    /// <summary>The result of <see cref="Rackspace.ConfirmResizedServer"/>.</summary>
    void OnConfirmResizedServer(RackspaceEventType eventType, string htmlAdditionalInfo) => ExecuteDefault("OnConfirmResizedServer", eventType, htmlAdditionalInfo);

    /// <summary>The result of <see cref="Rackspace.RevertResizedServer"/>.</summary>
    void OnRevertResizedServer(RackspaceEventType eventType, string htmlAdditionalInfo) => ExecuteDefault("OnRevertResizedServer", eventType, htmlAdditionalInfo);

    /// <summary>The result of <see cref="Rackspace.ListFlavors"/>.</summary>
    void OnListFlavorsResult(RackspaceEventType eventType, string htmlAdditionalInfo) => ExecuteDefault("OnListFlavorsResult", eventType, htmlAdditionalInfo);

    /// <summary>The result of <see cref="Rackspace.GetFlavorDetails"/>.</summary>
    void OnGetFlavorDetailsResult(RackspaceEventType eventType, string htmlAdditionalInfo) => ExecuteDefault("OnGetFlavorDetailsResult", eventType, htmlAdditionalInfo);

    /// <summary>The result of <see cref="Rackspace.ListImages"/>.</summary>
    void OnListImagesResult(RackspaceEventType eventType, string htmlAdditionalInfo) => ExecuteDefault("OnListImagesResult", eventType, htmlAdditionalInfo);

    /// <summary>The result of <see cref="Rackspace.CreateImage"/>.</summary>
    void OnCreateImageResult(RackspaceEventType eventType, string htmlAdditionalInfo) => ExecuteDefault("OnCreateImageResult", eventType, htmlAdditionalInfo);

    /// <summary>The result of <see cref="Rackspace.GetImageDetails"/>.</summary>
    void OnGetImageDetailsResult(RackspaceEventType eventType, string htmlAdditionalInfo) => ExecuteDefault("OnGetImageDetailsResult", eventType, htmlAdditionalInfo);

    /// <summary>The result of <see cref="Rackspace.DeleteImage"/>.</summary>
    void OnDeleteImageResult(RackspaceEventType eventType, string htmlAdditionalInfo) => ExecuteDefault("OnDeleteImageResult", eventType, htmlAdditionalInfo);

    /// <summary>The result of <see cref="Rackspace.ListSharedIPGroups"/>.</summary>
    void OnListSharedIPGroups(RackspaceEventType eventType, string htmlAdditionalInfo) => ExecuteDefault("OnListSharedIPGroups", eventType, htmlAdditionalInfo);

    /// <summary>The result of <see cref="Rackspace.ListSharedIPGroupsWithDetails"/>.</summary>
    void OnListSharedIPGroupsWithDetails(RackspaceEventType eventType, string htmlAdditionalInfo) => ExecuteDefault("OnListSharedIPGroupsWithDetails", eventType, htmlAdditionalInfo);

    /// <summary>The result of <see cref="Rackspace.CreateSharedIPGroup"/>.</summary>
    void OnCreateSharedIPGroup(RackspaceEventType eventType, string htmlAdditionalInfo) => ExecuteDefault("OnCreateSharedIPGroup", eventType, htmlAdditionalInfo);

    /// <summary>The result of <see cref="Rackspace.GetSharedIPGroupDetails"/>.</summary>
    void OnGetSharedIPGroupDetails(RackspaceEventType eventType, string htmlAdditionalInfo) => ExecuteDefault("OnGetSharedIPGroupDetails", eventType, htmlAdditionalInfo);

    /// <summary>The result of <see cref="Rackspace.DeleteSharedIPGroup"/>.</summary>
    void OnDeleteSharedIPGroup(RackspaceEventType eventType, string htmlAdditionalInfo) => ExecuteDefault("OnDeleteSharedIPGroup", eventType, htmlAdditionalInfo);

    /// <summary>OnConnectionAttemptFailure: the operation could not connect to <paramref name="url"/>; nothing by default.</summary>
    void OnConnectionAttemptFailure(RackspaceOperationType operationType, string url)
    {
    }
}
