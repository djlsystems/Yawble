namespace Yawble.Core.Companion;

/// <summary>What a paired external client may do. Cumulative: Read &lt; Act &lt; Destructive.</summary>
public enum CompanionLevel { Read = 1, Act = 2, Destructive = 3 }

public static class CompanionLevels
{
    public static bool TryParse(string? text, out CompanionLevel level)
    {
        switch (text)
        {
            case "read": level = CompanionLevel.Read; return true;
            case "act": level = CompanionLevel.Act; return true;
            case "destructive": level = CompanionLevel.Destructive; return true;
            default: level = default; return false;
        }
    }

    public static string Name(CompanionLevel level) => level switch
    {
        CompanionLevel.Read => "read",
        CompanionLevel.Act => "act",
        CompanionLevel.Destructive => "destructive",
        _ => throw new ArgumentOutOfRangeException(nameof(level)),
    };

    public static string Describe(CompanionLevel level) => level switch
    {
        CompanionLevel.Read => "Can look at teams, boards, backlog, documents and status. Changes nothing.",
        CompanionLevel.Act => "Can also start work (tell and answer agents, dispatch backlog items, nudge workflows), pause and resume teams, and manage the backlog and board; these run right away.",
        CompanionLevel.Destructive => "Can also ask for risky actions, for example git merges, pushes and branch deletes, pull requests, creating, archiving or deleting a team, backups and restores, the doctor's fixes or closing a workflow; each one still waits for your click in the app.",
        _ => throw new ArgumentOutOfRangeException(nameof(level)),
    };
}
