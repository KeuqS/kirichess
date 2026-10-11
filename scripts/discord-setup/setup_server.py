"""One-shot Discord server setup for the KiriChess community server.

Creates/updates roles, channels, permission overwrites, invite
restrictions, and baseline AutoMod rules via the Discord REST API.
Safe to re-run: existing roles/channels are matched by name and updated
in place instead of duplicated.

Usage:
    pip install -r requirements.txt
    cp .env.example .env   # fill in DISCORD_BOT_TOKEN and DISCORD_GUILD_ID
    python setup_server.py
"""
import os
import sys
import time

import requests
from dotenv import load_dotenv

load_dotenv()

BOT_TOKEN = os.environ["DISCORD_BOT_TOKEN"]
GUILD_ID = os.environ["DISCORD_GUILD_ID"]
API = "https://discord.com/api/v10"
HEADERS = {"Authorization": f"Bot {BOT_TOKEN}", "Content-Type": "application/json"}

PERM = {
    "CREATE_INSTANT_INVITE": 1 << 0,
    "KICK_MEMBERS": 1 << 1,
    "BAN_MEMBERS": 1 << 2,
    "ADMINISTRATOR": 1 << 3,
    "VIEW_AUDIT_LOG": 1 << 7,
    "VIEW_CHANNEL": 1 << 10,
    "SEND_MESSAGES": 1 << 11,
    "MANAGE_MESSAGES": 1 << 13,
    "ADD_REACTIONS": 1 << 6,
    "EMBED_LINKS": 1 << 14,
    "ATTACH_FILES": 1 << 15,
    "READ_MESSAGE_HISTORY": 1 << 16,
    "MANAGE_NICKNAMES": 1 << 27,
    "MANAGE_THREADS": 1 << 34,
    "CREATE_PUBLIC_THREADS": 1 << 35,
    "MODERATE_MEMBERS": 1 << 40,
}

CHANNEL_TYPE_TEXT = 0
CHANNEL_TYPE_CATEGORY = 4
CHANNEL_TYPE_ANNOUNCEMENT = 5

OVERWRITE_ROLE = 0


def perms(*names):
    total = 0
    for name in names:
        total |= PERM[name]
    return str(total)


def request(method, path, **kwargs):
    resp = requests.request(method, f"{API}{path}", headers=HEADERS, **kwargs)
    if resp.status_code == 429:
        retry_after = resp.json().get("retry_after", 1)
        time.sleep(retry_after)
        return request(method, path, **kwargs)
    if resp.status_code >= 400:
        raise RuntimeError(f"{method} {path} -> {resp.status_code}: {resp.text}")
    return resp.json() if resp.text else None


def get_roles():
    return request("GET", f"/guilds/{GUILD_ID}/roles")


def ensure_role(name, permissions, color=0, hoist=True):
    roles = get_roles()
    existing = next((r for r in roles if r["name"] == name), None)
    payload = {"name": name, "permissions": permissions, "color": color, "hoist": hoist}
    if existing:
        print(f"Updating role: {name}")
        return request("PATCH", f"/guilds/{GUILD_ID}/roles/{existing['id']}", json=payload)
    print(f"Creating role: {name}")
    return request("POST", f"/guilds/{GUILD_ID}/roles", json=payload)


def set_everyone_permissions(permissions):
    everyone = next(r for r in get_roles() if r["name"] == "@everyone")
    print("Updating @everyone base permissions")
    return request(
        "PATCH", f"/guilds/{GUILD_ID}/roles/{everyone['id']}", json={"permissions": permissions}
    )


def get_channels():
    return request("GET", f"/guilds/{GUILD_ID}/channels")


def ensure_category(name):
    channels = get_channels()
    existing = next(
        (c for c in channels if c["name"] == name and c["type"] == CHANNEL_TYPE_CATEGORY), None
    )
    if existing:
        return existing
    print(f"Creating category: {name}")
    return request(
        "POST", f"/guilds/{GUILD_ID}/channels", json={"name": name, "type": CHANNEL_TYPE_CATEGORY}
    )


def ensure_channel(name, channel_type, topic, parent_id=None, overwrites=None):
    channels = get_channels()
    existing = next((c for c in channels if c["name"] == name and c["type"] == channel_type), None)
    payload = {
        "name": name,
        "type": channel_type,
        "topic": topic,
        "parent_id": parent_id,
        "permission_overwrites": overwrites or [],
    }
    if existing:
        print(f"Updating channel: {name}")
        return request("PATCH", f"/channels/{existing['id']}", json=payload)
    print(f"Creating channel: {name}")
    return request("POST", f"/guilds/{GUILD_ID}/channels", json=payload)


def delete_channel(name, channel_type):
    channels = get_channels()
    existing = next((c for c in channels if c["name"] == name and c["type"] == channel_type), None)
    if existing:
        print(f"Deleting legacy channel: {name}")
        request("DELETE", f"/channels/{existing['id']}")


def fix_channel_overwrites(name, overwrites):
    channels = get_channels()
    existing = next((c for c in channels if c["name"] == name), None)
    if existing:
        print(f"Fixing overwrites on channel: {name}")
        request("PATCH", f"/channels/{existing['id']}", json={"permission_overwrites": overwrites})


def overwrite(role_id, allow=(), deny=()):
    return {
        "id": role_id,
        "type": OVERWRITE_ROLE,
        "allow": perms(*allow) if allow else "0",
        "deny": perms(*deny) if deny else "0",
    }


def ensure_automod_rule(name, trigger_type, trigger_metadata, actions):
    existing_rules = request("GET", f"/guilds/{GUILD_ID}/auto-moderation/rules")
    existing = next((r for r in existing_rules if r["name"] == name), None)
    payload = {
        "name": name,
        "event_type": 1,
        "trigger_type": trigger_type,
        "trigger_metadata": trigger_metadata,
        "actions": actions,
        "enabled": True,
    }
    if existing:
        print(f"Updating AutoMod rule: {name}")
        return request(
            "PATCH", f"/guilds/{GUILD_ID}/auto-moderation/rules/{existing['id']}", json=payload
        )
    print(f"Creating AutoMod rule: {name}")
    return request("POST", f"/guilds/{GUILD_ID}/auto-moderation/rules", json=payload)


def main():
    everyone_id = GUILD_ID

    set_everyone_permissions(
        perms(
            "VIEW_CHANNEL",
            "SEND_MESSAGES",
            "ADD_REACTIONS",
            "EMBED_LINKS",
            "ATTACH_FILES",
            "READ_MESSAGE_HISTORY",
            "CREATE_PUBLIC_THREADS",
        )
    )

    founder = ensure_role("Founder", perms("ADMINISTRATOR"), color=0xE74C3C)
    moderator = ensure_role(
        "Moderator",
        perms(
            "KICK_MEMBERS",
            "BAN_MEMBERS",
            "MANAGE_MESSAGES",
            "MANAGE_NICKNAMES",
            "MANAGE_THREADS",
            "MODERATE_MEMBERS",
            "VIEW_AUDIT_LOG",
            "CREATE_INSTANT_INVITE",
        ),
        color=0x3498DB,
    )
    playtester = ensure_role("Playtester", "0", color=0x2ECC71)

    print("Restricting invite creation to Moderator+")

    delete_channel("announcements", CHANNEL_TYPE_TEXT)
    delete_channel("looking-for-game", CHANNEL_TYPE_TEXT)

    fix_channel_overwrites(
        "moderator-only",
        [
            overwrite(everyone_id, deny=["VIEW_CHANNEL"]),
            overwrite(moderator["id"], allow=["VIEW_CHANNEL", "SEND_MESSAGES"]),
            overwrite(founder["id"], allow=["VIEW_CHANNEL", "SEND_MESSAGES"]),
        ],
    )

    announcements_overwrites = [
        overwrite(everyone_id, deny=["SEND_MESSAGES", "CREATE_PUBLIC_THREADS"]),
        overwrite(moderator["id"], allow=["SEND_MESSAGES"]),
        overwrite(founder["id"], allow=["SEND_MESSAGES"]),
    ]
    ensure_channel(
        "announcements",
        CHANNEL_TYPE_ANNOUNCEMENT,
        "Official updates from the team - new builds, patch notes, events. Founders/Mods only post here.",
        overwrites=announcements_overwrites,
    )
    ensure_channel(
        "general",
        CHANNEL_TYPE_TEXT,
        "Main hangout for anything chess-related. Say hi, chat, share whatever.",
    )
    ensure_channel(
        "looking-to-play",
        CHANNEL_TYPE_TEXT,
        "Post here to find an opponent. Format: Game - Time - Region. Keep it short and current.",
    )
    ensure_channel(
        "feedback",
        CHANNEL_TYPE_TEXT,
        "Bugs, suggestions, and thoughts on the game go here. Be specific - screenshots/steps to reproduce help a lot.",
    )
    ensure_channel(
        "off-topic",
        CHANNEL_TYPE_TEXT,
        "Anything not chess-related - memes, other games, life. Keep it civil.",
    )

    playtest_category = ensure_category("PLAYTEST")
    playtest_overwrites = [
        overwrite(everyone_id, deny=["VIEW_CHANNEL"]),
        overwrite(playtester["id"], allow=["VIEW_CHANNEL", "SEND_MESSAGES"]),
        overwrite(moderator["id"], allow=["VIEW_CHANNEL", "SEND_MESSAGES"]),
        overwrite(founder["id"], allow=["VIEW_CHANNEL", "SEND_MESSAGES"]),
    ]
    ensure_channel(
        "playtest-builds",
        CHANNEL_TYPE_TEXT,
        "Latest test builds and patch notes for playtesters.",
        parent_id=playtest_category["id"],
        overwrites=playtest_overwrites,
    )
    ensure_channel(
        "playtest-chat",
        CHANNEL_TYPE_TEXT,
        "Discussion for active playtesters.",
        parent_id=playtest_category["id"],
        overwrites=playtest_overwrites,
    )

    ensure_automod_rule(
        "Block spam content",
        trigger_type=3,
        trigger_metadata={},
        actions=[{"type": 1, "metadata": {}}],
    )
    ensure_automod_rule(
        "Block mass mentions",
        trigger_type=5,
        trigger_metadata={"mention_total_limit": 5},
        actions=[{"type": 1, "metadata": {}}],
    )

    print("\nDone. Still manual: enabling Community, the rules/welcome wizard, and verification level.")


if __name__ == "__main__":
    try:
        main()
    except RuntimeError as exc:
        print(f"Error: {exc}", file=sys.stderr)
        sys.exit(1)
