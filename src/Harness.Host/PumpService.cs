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

/// <summary>
/// Drives delivery. A plain loop over the subscribers, idling when there was nothing - with one
/// process and one database that is a complete queue, and no broker is being done without.
/// </summary>
internal sealed class PumpService(
    ContainerHost host, PumpHeartbeat heartbeat, ILogger<PumpService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // Every pass, failed or not: /healthz asks whether the loop is turning, and a pass that
            // threw is logged below and followed by the next one.
            heartbeat.Beat();
            var delivered = 0;

            try
            {
                delivered = await host.PumpOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // The pump is the only thing delivering anything. It has to outlive any single
                // failure, or one bad message stops every container permanently.
                logger.LogWarning(ex, "A delivery pass failed.");
            }

            if (delivered == 0)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }
}
