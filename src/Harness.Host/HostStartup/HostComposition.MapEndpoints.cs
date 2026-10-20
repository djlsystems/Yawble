using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.SignalR;
using ModelContextProtocol.AspNetCore;
using Harness.Containers;
using Harness.Backlog;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Harness.Host.Capacity;
using Harness.Host.Solutions;
using Harness.Identity;
using Harness.Kanban;
using Harness.Messaging;
using Harness.Skills;

namespace Harness.Host;

internal sealed partial class HostComposition
{
    internal void MapEndpoints()
    {
        OverviewEndpoints.Map(app, tenantSettings);
        AuthEndpoints.Map(app);
        UserEndpoints.Map(app);
        AgentEndpoints.Map(app, dataRoot);
        PluginEndpoints.Map(app);
        SolutionEndpoints.Map(app);
        ConnectionEndpoints.Map(app);
        RepoEndpoints.Map(app);
        KeyEndpoints.Map(app);
        FileSystemEndpoints.Map(app);
        SkillsEndpoints.Map(app);
        LeaseEndpoints.Map(app);
        KanbanEndpoints.Map(app);
        BacklogEndpoints.Map(app);
        NeedsYou.Map(app);
        BacklogEndpoints.MapTeamScoped(app);
        DiagnosticsEndpoints.Map(app);
        LiveViewEndpoints.Map(app);
        TeamEndpoints.Map(app);
        TriggerEndpoints.Map(app);
        MemberEndpoints.Map(app);
        ConciergeEndpoints.Map(app);
        WorkflowEndpoints.Map(app);
        DocumentsEndpoints.Map(app);
        MemberReportEndpoints.Map(app, tenantSettings);

        // The console is on a RAW WebSocket, not the SignalR hub. SignalR is message-oriented; a PTY is a
        // bidirectional byte stream with binary frames, and forcing one through the other would mean framing
        // bytes that are already framed.
        app.UseWebSockets();
    }
}
