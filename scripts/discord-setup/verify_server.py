"""Read-only check of the KiriChess Discord server against the intended setup.

Usage:
    python verify_server.py
"""
import os

import requests
from dotenv import load_dotenv

load_dotenv()

BOT_TOKEN = os.environ["DISCORD_BOT_TOKEN"]
GUILD_ID = os.environ["DISCORD_GUILD_ID"]
API = "https://discord.com/api/v10"
HEADERS = {"Authorization": f"Bot {BOT_TOKEN}"}

CHANNEL_TYPES = {0: "text", 4: "category", 5: "announcement"}


def get(path):
    resp = requests.get(f"{API}{path}", headers=HEADERS)
    resp.raise_for_status()
    return resp.json()


def main():
    guild = get(f"/guilds/{GUILD_ID}")
    roles = get(f"/guilds/{GUILD_ID}/roles")
    channels = get(f"/guilds/{GUILD_ID}/channels")
    automod = get(f"/guilds/{GUILD_ID}/auto-moderation/rules")
    role_names = {r["id"]: r["name"] for r in roles}

    print("=== Guild ===")
    print(f"Name: {guild['name']}")
    print(f"Community enabled: {'COMMUNITY' in guild['features']}")
    print(f"Verification level: {guild['verification_level']} (0=None 1=Low 2=Medium 3=High 4=Highest)")
    print(f"Rules channel set: {guild.get('rules_channel_id') is not None}")
    print(f"Public updates channel set: {guild.get('public_updates_channel_id') is not None}")

    print("\n=== Roles ===")
    for r in sorted(roles, key=lambda r: -r["position"]):
        print(f"  {r['name']:<12} pos={r['position']:<3} perms={r['permissions']}")

    print("\n=== Channels ===")
    by_parent = {}
    for c in channels:
        by_parent.setdefault(c.get("parent_id"), []).append(c)
    def print_overwrites(c):
        for ow in c.get("permission_overwrites", []):
            role = role_names.get(ow["id"], ow["id"])
            allow, deny = int(ow["allow"]), int(ow["deny"])
            if allow or deny:
                print(f"      {role}: allow={allow} deny={deny}")
        if not c.get("permission_overwrites"):
            print("      (no overwrites - inherits category/@everyone defaults)")

    categories = [c for c in channels if c["type"] == 4]
    for cat in categories:
        print(f"[{cat['name']}]")
        for c in sorted(by_parent.get(cat["id"], []), key=lambda c: c["position"]):
            topic = c.get("topic") or "(no topic)"
            print(f"  #{c['name']} ({CHANNEL_TYPES.get(c['type'], c['type'])}) - {topic}")
            print_overwrites(c)
    for c in sorted(by_parent.get(None, []), key=lambda c: c["position"]):
        if c["type"] == 4:
            continue
        topic = c.get("topic") or "(no topic)"
        print(f"#{c['name']} ({CHANNEL_TYPES.get(c['type'], c['type'])}) - {topic}")
        print_overwrites(c)

    print("\n=== AutoMod rules ===")
    if not automod:
        print("  none")
    for rule in automod:
        print(f"  {rule['name']} enabled={rule['enabled']}")


if __name__ == "__main__":
    main()
