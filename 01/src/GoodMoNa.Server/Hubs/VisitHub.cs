using Microsoft.AspNetCore.SignalR;

namespace GoodMoNa.Server.Hubs;

/// <summary>
/// 보고 앱은 보고 있는 방문의 그룹에 들어가 "사진 도착"을 받고,
/// 원장 앱(2번 과제)은 director 그룹에 들어가 "새 보고"를 받습니다.
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
    }

    public static string VisitGroup(int visitId) => $"visit:{visitId}";

    public Task JoinVisit(int visitId) => Groups.AddToGroupAsync(Context.ConnectionId, VisitGroup(visitId));

    public Task LeaveVisit(int visitId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, VisitGroup(visitId));

    public Task JoinDirector() => Groups.AddToGroupAsync(Context.ConnectionId, DirectorGroup);
}
