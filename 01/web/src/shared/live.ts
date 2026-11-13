import * as signalR from "@microsoft/signalr";

/**
 * SignalR 연결을 만들고 머리띠의 연결 표시를 갱신합니다.
 * 끊기면 자동 재연결하고, 다시 붙을 때마다 onReady를 불러 그룹 재가입·데이터 다시 읽기를 하게 합니다.
 */
export function connectLive(statusEl: HTMLElement, onReady: (hub: signalR.HubConnection) => Promise<void>): signalR.HubConnection {
  const hub = new signalR.HubConnectionBuilder()
    .withUrl("/hubs/visits")
    .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
    .configureLogging(signalR.LogLevel.Warning)
    .build();

  const set = (state: "connecting" | "connected" | "disconnected", text: string) => {
    statusEl.dataset.state = state;
    statusEl.textContent = text;
  };

  const start = async (): Promise<void> => {
    try {
      set("connecting", "연결 중");
      await hub.start();
      set("connected", "실시간");
      await onReady(hub);
    } catch {
      set("disconnected", "연결 끊김");
      window.setTimeout(start, 5000);
    }
  };

  hub.onreconnecting(() => set("connecting", "다시 연결 중"));
  hub.onreconnected(async () => {
    set("connected", "실시간");
    await onReady(hub);
  });
  hub.onclose(() => {
    set("disconnected", "연결 끊김");
    window.setTimeout(start, 5000);
  });

  // 폰·아이패드가 잠들었다 깨어나면 연결이 죽어 있는 경우가 많아, 화면이 다시 보일 때 확인합니다.
  document.addEventListener("visibilitychange", () => {
    if (document.visibilityState === "visible" && hub.state === signalR.HubConnectionState.Disconnected) void start();
  });

  void start();
  return hub;
}

export const isConnected = (hub: signalR.HubConnection): boolean => hub.state === signalR.HubConnectionState.Connected;
