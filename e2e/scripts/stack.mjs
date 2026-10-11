// Local convenience only, CI runs these steps inline so each one fails visibly on its own line.
import { spawnSync } from "node:child_process";
import path from "node:path";
import { fileURLToPath } from "node:url";

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..", "..");
const COMPOSE = ["compose", "-f", "docker-compose.prod.yml", "-f", "docker-compose.e2e.yml"];

function run(args) {
  const r = spawnSync("docker", args, { cwd: ROOT, stdio: "inherit", shell: false });
  if (r.status !== 0) process.exit(r.status ?? 1);
}

const cmd = process.argv[2];
if (cmd === "up") {
  run([...COMPOSE, "up", "-d", "--build"]);
  // --build here too: `up` above only rebuilds the services it starts (backend, frontend), never
  // the profile-gated migrate service. Without it, `run` silently reuses whatever kirichess-migrate
  // image already exists, which on a machine that ran this weeks ago is a stale binary missing
  // every migration added since, so the schema ends up behind the code and nothing says so.
  run([...COMPOSE, "--profile", "migrate", "run", "--rm", "--build", "migrate"]);
} else if (cmd === "down") {
  // -v drops the kirichess-data volume: a local re-run must not inherit the previous run's accounts.
  run([...COMPOSE, "down", "-v"]);
} else if (cmd === "logs") {
  run([...COMPOSE, "logs", "--no-color", "backend"]);
} else {
  console.error("usage: node scripts/stack.mjs <up|down|logs>");
  process.exit(2);
}
