using GoodMoNa.Server.Auth;
using Microsoft.AspNetCore.SignalR;

namespace GoodMoNa.Server.Hubs;

/// <summary>
/// 보고 앱은 보고 있는 방문의 그룹에 들어가 "사진 도착"을 받고,
/// 원장 화면(/director)은 director 그룹에 들어가 "새 보고"를 받습니다.
/// </summary>
public sealed class VisitHub : Hub
{
    public const string Path = "/hubs/visits";
    public const string DirectorGroup = "director";

    public static class Events
    {
        public const string PhotoArrived = "PhotoArrived";
        public const string VisitReported = "VisitReported";
        public const string ReportSubmitted = "ReportSubmitted";
        public const string NoteUpdated = "NoteUpdated";
        public const string NoteDeleted = "NoteDeleted";
    }

    public static string VisitGroup(int visitId) => $"visit:{visitId}";

    public Task JoinVisit(int visitId) => Groups.AddToGroupAsync(Context.ConnectionId, VisitGroup(visitId));

    public Task LeaveVisit(int visitId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, VisitGroup(visitId));

    /// <summary>새 보고 알림은 원장·대표원장만 받습니다.</summary>
    public Task JoinDirector()
    {
        if (Context.User is null || !Policies.DirectorRoles.Any(Context.User.IsInRole))
            throw new HubException("원장 화면 알림은 원장·대표원장만 받을 수 있습니다.");
        return Groups.AddToGroupAsync(Context.ConnectionId, DirectorGroup);
    }
}
