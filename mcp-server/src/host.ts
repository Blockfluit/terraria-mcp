/**
 * Resolve the host running Terraria. Under WSL the Windows host is the default
 * gateway, which we read from /proc/net/route (gateway is little-endian hex).
 */
export function resolveHost(env: NodeJS.ProcessEnv, routeTable: string): string {
  const fromEnv = env.TERRARIA_HOST?.trim();
  if (fromEnv) return fromEnv;
  return parseDefaultGateway(routeTable) ?? "127.0.0.1";
}

function parseDefaultGateway(routeTable: string): string | undefined {
  for (const line of routeTable.split("\n").slice(1)) {
    const cols = line.trim().split(/\s+/);
    if (cols.length < 3) continue;
    const [, destination, gateway] = cols;
    if (destination !== "00000000" || !/^[0-9A-Fa-f]{8}$/.test(gateway) || gateway === "00000000") continue;
    const bytes: number[] = [];
    for (let i = 6; i >= 0; i -= 2) bytes.push(parseInt(gateway.slice(i, i + 2), 16));
    return bytes.join(".");
  }
  return undefined;
}
