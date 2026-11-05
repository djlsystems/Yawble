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
    internal void Build()
    {
        app = builder.Build();

        // THE LOG STAMPS ITS ROWS BY THE HOST'S CLOCK. The store is made before the container exists,
        // so its clock is bound here, once, before anything runs: a trigger's spend today compares
        // those stamps with the same clock's "now" (TriggerCost), and both must be one clock.
        var clock = app.Services.GetRequiredService<TimeProvider>();
        switch (app.Services.GetRequiredService<IMessageLog>())
        {
            case SqliteMessageStore sqlite: sqlite.Clock = clock; break;
            case Harness.SqlServer.Messaging.SqlServerMessageStore sqlServer: sqlServer.Clock = clock; break;
        }

        // A CONTROL LOCK LOST BEFORE THE APP EXISTED stops it as soon as it starts: the lock's callback
        // had no app to stop, and a Host that another took the database from must not go on serving.
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            if (controlLost) app.Lifetime.StopApplication();
        });
    }
}
