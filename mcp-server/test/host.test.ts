import { describe, expect, it } from "vitest";
import { resolveHost } from "../src/host.js";

const ROUTE_TABLE = [
  "Iface\tDestination\tGateway \tFlags\tRefCnt\tUse\tMetric\tMask\t\tMTU\tWindow\tIRTT",
  "eth0\t00000000\t0140A8C0\t0003\t0\t0\t0\t00000000\t0\t0\t0",
  "eth0\t0040A8C0\t00000000\t0001\t0\t0\t0\t00F0FFFF\t0\t0\t0",
  "",
].join("\n");

describe("resolveHost", () => {
  it("uses TERRARIA_HOST when set", () => {
    expect(resolveHost({ TERRARIA_HOST: "10.0.0.5" }, ROUTE_TABLE)).toBe("10.0.0.5");
  });

  it("parses gateway 0140A8C0 as 192.168.64.1", () => {
    expect(resolveHost({}, ROUTE_TABLE)).toBe("192.168.64.1");
  });

  it("falls back to 127.0.0.1 on empty table", () => {
    expect(resolveHost({}, "")).toBe("127.0.0.1");
  });
});
