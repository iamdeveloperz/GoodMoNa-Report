import "../shared/base.css";
import "./home.css";
import { byId } from "../shared/format";

// 골격 확인용: 서버(/api/info)에 닿는지만 보여 줍니다.
async function check(): Promise<void> {
  try {
    const res = await fetch("/api/info");
    if (!res.ok) throw new Error(String(res.status));
    const info = (await res.json()) as { project: string; today: string };
    byId("status").textContent = "정상";
    byId("today").textContent = info.today;
  } catch {
    byId("status").textContent = "연결 안 됨";
  }
}

void check();
