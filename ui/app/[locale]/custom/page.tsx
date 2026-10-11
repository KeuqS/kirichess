"use client";

import {
  useEffect,
  useMemo,
  useRef,
  useState,
  type CSSProperties,
  type ReactNode,
} from "react";
import { useRouter } from "@/i18n/navigation";
import { useTranslations } from "next-intl";
import { IconCheck, IconCopy, IconX } from "@tabler/icons-react";
import { RequireAuth } from "@/components/RequireAuth";
import { NavBar } from "@/components/NavBar";
import { Footer } from "@/components/Footer";
import { AlreadyInGameNotice } from "@/components/AlreadyInGameNotice";
import { Card } from "@/components/Card";
import { SkeletonStatus } from "@/components/SkeletonRows";
import { TabChip } from "@/components/TabChip";
import { PoolBadge } from "@/components/PoolBadge";
import { createGameConnection, stopWhenSettled } from "@/lib/realtime";
import {
  ApiError,
  cancelChallenge,
  createChallenge,
  createGame,
  getMyChallenges,
} from "@/lib/api";
import { SITE_URL } from "@/lib/site";
import { usePointerRangeDrag } from "@/hooks/usePointerRangeDrag";
import type { ChallengeAcceptedPayload, ChallengeDto } from "@/lib/types";
import {
  DEFAULT_CUSTOM_CONFIG,
  MOVE_COST_MODES,
  presetToCustomConfig,
  type CustomGameConfig,
  type PieceInts5,
  type PieceInts6,
} from "@/lib/customConfig";
import {
  PRESET_ORDER,
  PRESETS,
  economyOf,
  type MoveCostMode,
  type PresetKey,
} from "@/lib/presets";

const CAPTURE_PIECES: (keyof PieceInts5)[] = [
  "pawn",
  "knight",
  "bishop",
  "rook",
  "queen",
];
const TOLL_PIECES: (keyof PieceInts6)[] = [
  "pawn",
  "knight",
  "bishop",
  "rook",
  "queen",
  "king",
];
const PIECE_ICON: Record<keyof PieceInts6, string> = {
  pawn: "♟",
  knight: "♞",
  bishop: "♝",
  rook: "♜",
  queen: "♛",
  king: "♚",
};

type Translator = (
  key: string,
  values?: Record<string, string | number>,
) => string;

const CLOCK_CHIPS = [
  { label: "1m", seconds: 60 },
  { label: "3m", seconds: 180 },
  { label: "5m", seconds: 300 },
  { label: "10m", seconds: 600 },
  { label: "15m", seconds: 900 },
];

interface ValidationIssue {
  field: string;
  message: string;
}

function validateConfig(
  c: CustomGameConfig,
  t: Translator,
  pieceLabel: Record<keyof PieceInts6, string>,
): ValidationIssue[] {
  const issues: ValidationIssue[] = [];
  const nonNeg = (v: number, field: string) => {
    if (!Number.isFinite(v) || v < 0) {
      issues.push({ field, message: t("fieldCannotBeNegative", { field }) });
    }
  };

  if (!Number.isFinite(c.startTimeSeconds) || c.startTimeSeconds < 1) {
    issues.push({
      field: "Start time",
      message: t("startTimeMinError"),
    });
  }
  nonNeg(c.baseIncrement, t("baseIncrementLabel"));
  if (!Number.isFinite(c.economyScale) || c.economyScale < 1) {
    issues.push({
      field: "Economy scale",
      message: t("economyScaleMinError"),
    });
  }
  nonNeg(c.captureMult, t("captureMultiplierFieldLabel"));
  for (const p of CAPTURE_PIECES)
    nonNeg(
      c.pieceValues[p],
      t("pieceCaptureValueField", { piece: pieceLabel[p] }),
    );
  for (const p of TOLL_PIECES)
    nonNeg(
      c.moveCostPerPiece[p],
      t("pieceTollField", { piece: pieceLabel[p] }),
    );
  nonNeg(c.castleCostFlat, t("castleCostFieldLabel"));
  nonNeg(c.moveCostFlat, t("flatMoveCostLabel"));
  nonNeg(c.moveCostPerSquare, t("perSquareMoveCostFieldLabel"));
  nonNeg(c.checkCheckerGain, t("checkCheckerGainFieldLabel"));
  nonNeg(c.checkVictimLoss, t("checkVictimLossFieldLabel"));
  nonNeg(c.doubleCheckChipMultiplier, t("doubleCheckMultiplierLabel"));
  nonNeg(c.promotionCostMult, t("promotionCostMultiplierLabel"));

  return issues;
}

function clampInt(
  raw: string,
  min: number,
  max: number,
  fallback: number,
): number {
  const n = parseInt(raw, 10);
  if (Number.isNaN(n)) return fallback;
  return Math.min(max, Math.max(min, n));
}

function NumberField({
  label,
  icon,
  value,
  onChange,
  min = 0,
  max = 999,
  suffix,
  disabled,
}: {
  label: string;
  icon?: string;
  value: number;
  onChange: (v: number) => void;
  min?: number;
  max?: number;
  suffix?: string;
  disabled?: boolean;
}) {
  return (
    <label className={`flex flex-col gap-1.5 ${disabled ? "opacity-40" : ""}`}>
      <span className="text-sm font-medium text-[var(--foreground)] inline-flex items-center gap-1.5">
        {icon && (
          <span className="inline-flex w-5 shrink-0 justify-center text-xl font-normal leading-none text-white">
            {icon}
          </span>
        )}
        {label}
      </span>
      <div className="flex items-center gap-2">
        <input
          type="number"
          value={Number.isFinite(value) ? value : min}
          min={min}
          max={max}
          disabled={disabled}
          onChange={(e) => onChange(clampInt(e.target.value, min, max, min))}
          className="w-full border border-[var(--border)] bg-[var(--background)] px-3 py-2 text-sm text-white focus:border-[var(--bounty)] focus:outline-none disabled:cursor-not-allowed"
        />
        {suffix && (
          <span className="shrink-0 text-sm text-muted">{suffix}</span>
        )}
      </div>
    </label>
  );
}

function ToggleField({
  label,
  hint,
  value,
  onChange,
}: {
  label: string;
  hint?: string;
  value: boolean;
  onChange: (v: boolean) => void;
}) {
  return (
    <button
      type="button"
      onClick={() => onChange(!value)}
      className="flex w-full items-center justify-between gap-4 border border-[var(--border)] px-3 py-2.5 text-left text-[var(--foreground)] transition hover:border-[var(--bounty)]"
    >
      <span>
        <span className="block text-sm font-medium">{label}</span>
        {hint && (
          <span className="block text-sm text-muted mt-0.5 leading-relaxed">
            {hint}
          </span>
        )}
      </span>
      <span
        className={`relative h-5 w-9 shrink-0 transition-colors ${
          value ? "bg-[var(--bounty)]" : "bg-[var(--border)]"
        }`}
      >
        <span
          className={`absolute top-0.5 h-4 w-4 bg-[var(--background)] transition-transform ${
            value ? "translate-x-[18px]" : "translate-x-0.5"
          }`}
        />
      </span>
    </button>
  );
}

function SectionCard({
  title,
  titleBadge,
  blurb,
  children,
}: {
  title: string;
  /** A live readout of what this section's controls currently compute to, e.g. the economy badge. */
  titleBadge?: ReactNode;
  blurb?: string;
  children: ReactNode;
}) {
  return (
    <Card>
      <div className="flex items-center justify-between gap-3">
        <h2 className="text-lg font-bold text-white">{title}</h2>
        {titleBadge}
      </div>
      {blurb && (
        <p className="mt-1 text-sm text-muted leading-relaxed">{blurb}</p>
      )}
      <div className="flex flex-col gap-4 mt-4">{children}</div>
    </Card>
  );
}

function CustomGameBuilder() {
  const t = useTranslations("custom");
  const tChallenge = useTranslations("ChallengeFriendPanel");
  const router = useRouter();

  const [config, setConfig] = useState<CustomGameConfig>(DEFAULT_CUSTOM_CONFIG);
  const [activeLoad, setActiveLoad] = useState<PresetKey | "defaults">(
    "defaults",
  );
  const [opponent, setOpponent] = useState<"someone" | "bot" | "local">(
    "someone",
  );
  const [botLevel, setBotLevel] = useState(1500);
  const botLevelDrag = usePointerRangeDrag({
    min: 400,
    max: 3000,
    step: 50,
    onChange: setBotLevel,
  });
  const [creating, setCreating] = useState(false);
  const [createError, setCreateError] = useState<string | null>(null);

  const [challenge, setChallenge] = useState<ChallengeDto | null>(null);
  const [loadingMine, setLoadingMine] = useState(true);
  const [cancelling, setCancelling] = useState(false);
  const [challengeCopied, setChallengeCopied] = useState(false);
  const [challengeClipboardFailed, setChallengeClipboardFailed] =
    useState(false);
  const challengeCopyTimeoutRef = useRef<ReturnType<typeof setTimeout> | null>(
    null,
  );
  const challengeLinkInputRef = useRef<HTMLInputElement>(null);
  const [nowTick, setNowTick] = useState(() => Date.now());

  useEffect(
    () => () => {
      if (challengeCopyTimeoutRef.current)
        clearTimeout(challengeCopyTimeoutRef.current);
    },
    [],
  );

  // Restores an open custom-config invite across a reload. Official-pool links stay on the lobby
  // panel; each surface only re-displays the kind it minted.
  useEffect(() => {
    let cancelled = false;
    getMyChallenges()
      .then((mine) => {
        if (cancelled) return;
        const open = mine.find(
          (c) => c.status === "Open" && c.pool === "custom",
        );
        if (open) {
          setChallenge(open);
          setOpponent("someone");
        }
      })
      .catch(() => {
        // Nothing to restore is not worth surfacing on mount.
      })
      .finally(() => {
        if (!cancelled) setLoadingMine(false);
      });
    return () => {
      cancelled = true;
    };
  }, []);

  useEffect(() => {
    const id = setInterval(() => setNowTick(Date.now()), 30_000);
    return () => clearInterval(id);
  }, []);

  const expiresInMinutes = challenge
    ? Math.max(
        0,
        Math.ceil((new Date(challenge.expiresAt).getTime() - nowTick) / 60000),
      )
    : 0;
  const localExpired = challenge !== null && expiresInMinutes <= 0;

  useEffect(() => {
    if (localExpired) setChallenge(null);
  }, [localExpired]);

  useEffect(() => {
    if (!challenge || challenge.status !== "Open") return;
    const watchToken = challenge.token;
    const conn = createGameConnection();

    conn.on("ChallengeAccepted", (p: ChallengeAcceptedPayload) => {
      if (p.token !== watchToken) return;
      router.push(`/game/${p.gameId}?preset=${p.pool}`);
    });

    async function watch() {
      try {
        await conn.invoke("WatchChallenge", watchToken);
      } catch {
        // Best-effort: a failed join is retried on reconnect, or by a reload.
      }
    }

    conn.onreconnected(() => {
      watch();
    });

    const started = conn
      .start()
      .then(watch)
      .catch(() => {
        // The link and Copy button still work with no live connection.
      });

    return () => {
      stopWhenSettled(conn, started, () =>
        conn.invoke("UnwatchChallenge", watchToken),
      );
    };
  }, [challenge?.token, challenge?.status, router]);

  const pieceLabel: Record<keyof PieceInts6, string> = {
    pawn: t("piecePawn"),
    knight: t("pieceKnight"),
    bishop: t("pieceBishop"),
    rook: t("pieceRook"),
    queen: t("pieceQueen"),
    king: t("pieceKing"),
  };

  const issues = useMemo(
    () => validateConfig(config, t, pieceLabel),
    [config, t],
  );
  const isValid = issues.length === 0;

  function patch(p: Partial<CustomGameConfig>) {
    setConfig((c) => ({ ...c, ...p }));
  }
  function patchPieceValue(piece: keyof PieceInts5, value: number) {
    setConfig((c) => ({
      ...c,
      pieceValues: { ...c.pieceValues, [piece]: value },
    }));
  }
  function patchMoveCostPerPiece(piece: keyof PieceInts6, value: number) {
    setConfig((c) => ({
      ...c,
      moveCostPerPiece: { ...c.moveCostPerPiece, [piece]: value },
    }));
  }
  function loadPreset(key: PresetKey) {
    setConfig(presetToCustomConfig(key));
    setActiveLoad(key);
  }
  function loadDefaults() {
    setConfig(DEFAULT_CUSTOM_CONFIG);
    setActiveLoad("defaults");
  }

  const economy = economyOf({
    captorGain: config.captureCaptorGainEnabled,
    victimLoss: config.captureVictimLossEnabled,
  });

  const perPieceRelevant =
    config.moveCostMode === "PerPiece" ||
    config.moveCostMode === "PerPieceXDistance";
  const flatRelevant = config.moveCostMode === "Flat";
  const perSquareRelevant =
    config.moveCostMode === "Distance" ||
    config.moveCostMode === "PerPieceXDistance";

  function tollAtCurrentSettings(piece: keyof PieceInts6): number {
    switch (config.moveCostMode) {
      case "None":
        return 0;
      case "Flat":
        return config.moveCostFlat;
      case "PerPiece":
        return config.moveCostPerPiece[piece];
      case "Distance":
        return config.moveCostPerSquare; // per square
      case "PerPieceXDistance":
        return config.moveCostPerPiece[piece]; // per square, multiplied by distance
    }
  }

  async function handleStart() {
    if (!isValid || creating || opponent === "someone") return;
    setCreating(true);
    setCreateError(null);
    try {
      const state = await createGame({
        opponentId: null,
        preset: null,
        rated: false,
        custom: config,
      });
      const q = new URLSearchParams({ preset: "custom" });
      if (opponent === "bot") {
        q.set("mode", "bot");
        q.set("level", String(botLevel));
      }
      router.push(`/game/${state.gameId}?${q.toString()}`);
    } catch (err) {
      setCreateError(
        err instanceof ApiError
          ? err.message
          : err instanceof Error
            ? err.message
            : t("createGameError"),
      );
      setCreating(false);
    }
  }

  async function handleCreateChallenge() {
    if (!isValid || creating) return;
    setCreating(true);
    setCreateError(null);
    try {
      const created = await createChallenge("custom", "Random", config);
      setChallenge(created);
    } catch (err) {
      setCreateError(
        err instanceof ApiError ? err.message : tChallenge("failedToCreate"),
      );
    } finally {
      setCreating(false);
    }
  }

  async function handleCancelChallenge() {
    if (!challenge) return;
    setCancelling(true);
    setCreateError(null);
    try {
      await cancelChallenge(challenge.token);
      setChallenge(null);
    } catch (err) {
      setCreateError(
        err instanceof ApiError ? err.message : tChallenge("failedToCancel"),
      );
    } finally {
      setCancelling(false);
    }
  }

  function markChallengeCopied() {
    setChallengeClipboardFailed(false);
    setChallengeCopied(true);
    if (challengeCopyTimeoutRef.current)
      clearTimeout(challengeCopyTimeoutRef.current);
    challengeCopyTimeoutRef.current = setTimeout(
      () => setChallengeCopied(false),
      2000,
    );
  }

  function copyChallengeFromInput(): boolean {
    const input = challengeLinkInputRef.current;
    if (!input) return false;
    input.focus();
    input.select();
    try {
      return document.execCommand("copy");
    } catch {
      return false;
    }
  }

  function handleCopyChallenge(url: string) {
    const writeText = navigator.clipboard?.writeText;
    if (writeText) {
      void writeText
        .call(navigator.clipboard, url)
        .then(markChallengeCopied, () => {
          if (copyChallengeFromInput()) markChallengeCopied();
          else setChallengeClipboardFailed(true);
        });
      return;
    }
    if (copyChallengeFromInput()) markChallengeCopied();
    else setChallengeClipboardFailed(true);
  }

  return (
    <div className="min-h-screen flex flex-col bg-[var(--background)] text-[var(--foreground)] font-sans selection:bg-[var(--bounty)] selection:text-[#282c34]">
      <NavBar />

      <main
        id="main-content"
        className="flex-1 page-width pt-10 pb-24 scroll-mt-24"
      >
        <div className="mb-8">
          <h1 className="text-3xl font-bold leading-tight tracking-tight mb-4 text-white text-balance">
            {t("pageTitle")}
          </h1>
        </div>

        {/* PRESET LOADER */}
        <div className="mb-8">
          <div className="flex items-center gap-4 mb-4">
            <span className="text-sm tracking-widest text-muted font-semibold">
              {t("loadStartingPoint")}
            </span>
            <div className="flex-1 h-px bg-[var(--border)]" />
          </div>
          <div className="flex flex-wrap gap-2">
            <TabChip active={activeLoad === "defaults"} onClick={loadDefaults}>
              {t("defaults")}
            </TabChip>
            {PRESET_ORDER.map((key) => (
              <TabChip
                key={key}
                active={activeLoad === key}
                onClick={() => loadPreset(key)}
              >
                {PRESETS[key].label}
              </TabChip>
            ))}
          </div>
        </div>

        <div className="grid grid-cols-1 lg:grid-cols-[1fr_340px] gap-6 items-start">
          {/* LEFT: GROUPED CONTROLS */}
          <div className="flex flex-col gap-6">
            <SectionCard title={t("clockSectionTitle")}>
              <div className="flex flex-wrap gap-2">
                {CLOCK_CHIPS.map((chip) => (
                  <TabChip
                    key={chip.label}
                    active={config.startTimeSeconds === chip.seconds}
                    onClick={() => patch({ startTimeSeconds: chip.seconds })}
                  >
                    {chip.label}
                  </TabChip>
                ))}
              </div>
              <div className="grid grid-cols-2 gap-4">
                <NumberField
                  label={t("startTimeLabel")}
                  value={config.startTimeSeconds}
                  min={1}
                  max={7200}
                  suffix={t("startTimeSuffix")}
                  onChange={(v) => patch({ startTimeSeconds: v })}
                />
                <NumberField
                  label={t("baseIncrementLabel")}
                  value={config.baseIncrement}
                  min={0}
                  max={60}
                  suffix={t("baseIncrementSuffix")}
                  onChange={(v) => patch({ baseIncrement: v })}
                />
              </div>
              <p className="text-sm text-muted leading-relaxed">
                {t("clockPerSide", {
                  minutes: Math.floor(config.startTimeSeconds / 60),
                  seconds: config.startTimeSeconds % 60,
                })}
              </p>
            </SectionCard>

            <SectionCard
              title={t("capturesSectionTitle")}
              titleBadge={<PoolBadge economy={economy} variant="plain" />}
            >
              <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
                <ToggleField
                  label={t("captorGainsLabel")}
                  value={config.captureCaptorGainEnabled}
                  onChange={(v) => patch({ captureCaptorGainEnabled: v })}
                />
                <ToggleField
                  label={t("victimLosesLabel")}
                  value={config.captureVictimLossEnabled}
                  onChange={(v) => patch({ captureVictimLossEnabled: v })}
                />
              </div>
              <div className="grid grid-cols-2 gap-4">
                <NumberField
                  label={t("captureMultiplierInputLabel")}
                  value={config.captureMult}
                  min={0}
                  max={100}
                  onChange={(v) => patch({ captureMult: v })}
                />
                <NumberField
                  label={t("economyScaleInputLabel")}
                  value={config.economyScale}
                  min={1}
                  max={20}
                  onChange={(v) => patch({ economyScale: v })}
                />
              </div>
              <div>
                <span className="text-sm font-semibold text-white">
                  {t("captureValuesHeading")}
                </span>
                <div className="grid grid-cols-2 md:grid-cols-5 gap-3 mt-2">
                  {CAPTURE_PIECES.map((p) => (
                    <NumberField
                      key={p}
                      icon={PIECE_ICON[p]}
                      label={pieceLabel[p]}
                      value={config.pieceValues[p]}
                      min={0}
                      max={50}
                      onChange={(v) => patchPieceValue(p, v)}
                    />
                  ))}
                </div>
              </div>
            </SectionCard>

            <SectionCard title={t("tollsSectionTitle")}>
              <label className="flex flex-col gap-1.5">
                <span className="text-sm font-medium text-[var(--foreground)]">
                  {t("moveCostModeLabel")}
                </span>
                <select
                  value={config.moveCostMode}
                  onChange={(e) =>
                    patch({ moveCostMode: e.target.value as MoveCostMode })
                  }
                  className="input-select w-full max-w-xs border border-[var(--border)] bg-[var(--background)] px-3 py-2 pr-10 text-sm text-white focus:border-[var(--bounty)] focus:outline-none"
                >
                  {MOVE_COST_MODES.map((m) => (
                    <option key={m} value={m}>
                      {m}
                    </option>
                  ))}
                </select>
              </label>

              <div className="grid grid-cols-2 gap-4">
                <NumberField
                  label={t("flatMoveCostLabel")}
                  value={config.moveCostFlat}
                  min={0}
                  max={50}
                  disabled={!flatRelevant}
                  onChange={(v) => patch({ moveCostFlat: v })}
                />
                <NumberField
                  label={t("perSquareCostInputLabel")}
                  value={config.moveCostPerSquare}
                  min={0}
                  max={50}
                  disabled={!perSquareRelevant}
                  onChange={(v) => patch({ moveCostPerSquare: v })}
                />
              </div>

              <div>
                <span className="text-sm font-semibold text-white">
                  {t("perPieceTollHeading")}
                </span>
                <div
                  className={`grid grid-cols-2 md:grid-cols-3 gap-3 mt-2 ${
                    perPieceRelevant ? "" : "opacity-40"
                  }`}
                >
                  {TOLL_PIECES.map((p) => (
                    <NumberField
                      key={p}
                      icon={PIECE_ICON[p]}
                      label={pieceLabel[p]}
                      value={config.moveCostPerPiece[p]}
                      min={0}
                      max={50}
                      disabled={!perPieceRelevant}
                      onChange={(v) => patchMoveCostPerPiece(p, v)}
                    />
                  ))}
                </div>
              </div>

              <NumberField
                label={t("castleCostInputLabel")}
                value={config.castleCostFlat}
                min={0}
                max={50}
                onChange={(v) => patch({ castleCostFlat: v })}
              />
            </SectionCard>

            <SectionCard title={t("checkChipSectionTitle")}>
              <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
                <NumberField
                  label={t("checkerGainInputLabel")}
                  value={config.checkCheckerGain}
                  min={0}
                  max={50}
                  onChange={(v) => patch({ checkCheckerGain: v })}
                />
                <NumberField
                  label={t("victimLossInputLabel")}
                  value={config.checkVictimLoss}
                  min={0}
                  max={50}
                  onChange={(v) => patch({ checkVictimLoss: v })}
                />
                <NumberField
                  label={t("doubleCheckMultiplierLabel")}
                  value={config.doubleCheckChipMultiplier}
                  min={0}
                  max={10}
                  onChange={(v) => patch({ doubleCheckChipMultiplier: v })}
                />
              </div>
            </SectionCard>

            <SectionCard title={t("repetitionGuardTitle")}>
              <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
                <ToggleField
                  label={t("repeatedPositionLabel")}
                  hint={t("repeatedPositionHint")}
                  value={config.repeatedPositionNullifiesTime}
                  onChange={(v) => patch({ repeatedPositionNullifiesTime: v })}
                />
                <ToggleField
                  label={t("threefoldLabel")}
                  hint={t("threefoldHint")}
                  value={config.threefoldRepetitionIsDraw}
                  onChange={(v) => patch({ threefoldRepetitionIsDraw: v })}
                />
              </div>
            </SectionCard>

            <SectionCard title={t("otherEndingsTitle")}>
              <NumberField
                label={t("promotionCostMultiplierLabel")}
                value={config.promotionCostMult}
                min={0}
                max={20}
                onChange={(v) => patch({ promotionCostMult: v })}
              />
              <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
                <ToggleField
                  label={t("flagFallLosesLabel")}
                  hint={t("flagFallLosesHint")}
                  value={config.flagFallLoses}
                  onChange={(v) => patch({ flagFallLoses: v })}
                />
                <ToggleField
                  label={t("flagVsInsufficientLabel")}
                  hint={t("flagVsInsufficientHint")}
                  value={config.flagVsInsufficientMaterialIsDraw}
                  onChange={(v) =>
                    patch({ flagVsInsufficientMaterialIsDraw: v })
                  }
                />
              </div>
            </SectionCard>
          </div>

          {/* RIGHT: OPPONENT + LIVE PREVIEW. Capped to the viewport and independently scrollable
              (same pattern as the rules page TOC sidebar) rather than just sticky: on a short
              screen this column is taller than the viewport, and a plain sticky element that
              overflows can pin the Start Game button below the fold with no way to reach it
              short of scrolling the whole page past the aside. */}
          <aside className="flex flex-col gap-4 lg:sticky lg:top-24 lg:max-h-[calc(100vh-6rem)] lg:overflow-y-auto">
            <Card padding="p-5">
              <div className="text-sm tracking-widest text-muted mb-3 font-semibold">
                {t("opponentHeading")}
              </div>
              <div className="flex flex-wrap gap-2">
                {(
                  [
                    ["someone", t("opponentSomeone")],
                    ["bot", t("opponentBot")],
                    ["local", t("opponentLocal")],
                  ] as const
                ).map(([kind, label]) => (
                  <TabChip
                    key={kind}
                    active={opponent === kind}
                    onClick={() => setOpponent(kind)}
                  >
                    {label}
                  </TabChip>
                ))}
              </div>
              {opponent === "bot" && (
                <div className="mt-4">
                  <div className="flex items-center justify-between text-sm text-muted mb-2">
                    <span>{t("botStrengthLabel")}</span>
                    <span className="text-white font-semibold">
                      {t("botStrengthElo", { level: botLevel })}
                    </span>
                  </div>
                  <input
                    ref={botLevelDrag.ref}
                    onPointerDown={botLevelDrag.onPointerDown}
                    type="range"
                    aria-label={t("botStrengthLabel")}
                    min={400}
                    max={3000}
                    step={50}
                    value={botLevel}
                    onChange={(e) => setBotLevel(parseInt(e.target.value, 10))}
                    className="range-fill w-full accent-[var(--bounty)]"
                    style={
                      {
                        "--range-progress": `${((botLevel - 400) / (3000 - 400)) * 100}%`,
                      } as CSSProperties
                    }
                  />
                </div>
              )}
              {opponent === "local" && (
                <p className="mt-3 text-sm text-muted leading-relaxed">
                  {t("opponentLocalHint")}
                </p>
              )}
              {opponent === "someone" &&
                (loadingMine ? (
                  <div className="mt-3">
                    <SkeletonStatus
                      label={tChallenge("loadingEllipsis")}
                      count={3}
                    />
                  </div>
                ) : challenge && !localExpired ? (
                  <div className="mt-4 flex flex-col gap-3">
                    <p className="text-sm font-semibold text-white">
                      {tChallenge("waitingForFriend")}
                    </p>
                    <p className="text-sm text-muted">
                      {t("pageTitle")} - {tChallenge("unrated")} -{" "}
                      {tChallenge("randomColour")}
                    </p>
                    <div className="flex items-center gap-2">
                      <input
                        ref={challengeLinkInputRef}
                        readOnly
                        value={`${SITE_URL}/challenge/${challenge.token}`}
                        aria-label={tChallenge("challengeLinkAriaLabel")}
                        onFocus={(e) => e.currentTarget.select()}
                        className="min-w-0 flex-1 truncate border border-[var(--border)] bg-[var(--background)] px-3 py-2 text-sm text-white"
                      />
                      <button
                        type="button"
                        onClick={() =>
                          handleCopyChallenge(
                            `${SITE_URL}/challenge/${challenge.token}`,
                          )
                        }
                        className="btn-secondary flex shrink-0 gap-1.5 px-3 py-1.5 text-sm"
                      >
                        {challengeCopied ? (
                          <IconCheck size={16} />
                        ) : (
                          <IconCopy size={16} />
                        )}
                        {challengeCopied
                          ? tChallenge("copied")
                          : tChallenge("copy")}
                      </button>
                    </div>
                    <span role="status" aria-live="polite" className="sr-only">
                      {challengeCopied ? tChallenge("copiedAnnouncement") : ""}
                    </span>
                    {challengeClipboardFailed && (
                      <p className="text-sm text-muted">
                        {tChallenge("clipboardFailed")}
                      </p>
                    )}
                    <p className="text-sm text-muted">
                      {tChallenge("expiresIn", { minutes: expiresInMinutes })}
                    </p>
                    <button
                      type="button"
                      onClick={handleCancelChallenge}
                      disabled={cancelling}
                      className="btn-danger flex gap-1.5 self-start px-3 py-1.5 text-sm"
                    >
                      <IconX size={14} />
                      {cancelling
                        ? tChallenge("cancelling")
                        : tChallenge("cancelChallenge")}
                    </button>
                  </div>
                ) : (
                  <p className="mt-3 text-sm text-muted leading-relaxed">
                    {tChallenge("sendALink")}
                  </p>
                ))}
            </Card>

            <Card padding="p-5">
              <div className="text-sm tracking-widest text-muted mb-3 font-semibold">
                {t("livePreviewHeading")}
              </div>

              <div className="flex items-center gap-2 mb-4">
                <PoolBadge economy={economy} variant="plain" />
                <span aria-hidden="true" className="text-muted">
                  -
                </span>
                <span className="text-sm text-muted">
                  {t("capturePreview", { mult: config.captureMult })}
                  {config.economyScale > 1
                    ? t("economyScaleSuffix", { scale: config.economyScale })
                    : ""}
                </span>
              </div>

              <div className="mb-4">
                <div className="text-sm text-muted mb-1.5">
                  {t("captureSwingHeading")}
                </div>
                <div className="flex flex-col gap-1 text-sm">
                  {CAPTURE_PIECES.map((p) => {
                    const swing =
                      config.pieceValues[p] *
                      config.captureMult *
                      config.economyScale;
                    return (
                      <div
                        key={p}
                        className="flex items-center justify-between"
                      >
                        <span className="inline-flex items-center gap-2 font-semibold text-white">
                          <span className="inline-flex w-5 shrink-0 justify-center text-xl leading-none">
                            {PIECE_ICON[p]}
                          </span>
                          {pieceLabel[p]}
                        </span>
                        <span
                          className={`font-medium ${
                            economy === "steal"
                              ? "text-[var(--economy-steal)]"
                              : economy === "bounty"
                                ? "text-[var(--economy-bounty)]"
                                : "text-[var(--economy-burn)]"
                          }`}
                        >
                          {economy === "steal"
                            ? "±"
                            : economy === "bounty"
                              ? "+"
                              : "−"}
                          {swing}s
                        </span>
                      </div>
                    );
                  })}
                </div>
              </div>

              <div className="mb-4">
                <div className="text-sm text-muted mb-1.5">
                  {t("tollAtCurrentSettingsHeading")}
                </div>
                <div className="flex flex-col gap-1 text-sm">
                  {TOLL_PIECES.map((p) => (
                    <div key={p} className="flex items-center justify-between">
                      <span className="inline-flex items-center gap-2 font-semibold text-white">
                        <span className="inline-flex w-5 shrink-0 justify-center text-xl leading-none">
                          {PIECE_ICON[p]}
                        </span>
                        {pieceLabel[p]}
                      </span>
                      <span className="font-medium text-white">
                        {tollAtCurrentSettings(p) * config.economyScale}s
                        {perSquareRelevant ? "/sq" : ""}
                      </span>
                    </div>
                  ))}
                  <div className="flex items-center justify-between">
                    <span className="font-semibold text-white">
                      {t("castlingRow")}
                    </span>
                    <span className="font-medium text-white">
                      {(config.moveCostMode === "None"
                        ? 0
                        : config.castleCostFlat * config.economyScale) + "s"}
                    </span>
                  </div>
                </div>
              </div>

              <div className="mb-4">
                <div className="text-sm text-muted mb-1.5">
                  {t("checkChipSectionTitle")}
                </div>
                <p className="text-sm text-muted leading-relaxed">
                  {t.rich("checkChipSummary", {
                    gain: (chunks) => (
                      <span className="text-[var(--gain)]">{chunks}</span>
                    ),
                    loss: (chunks) => (
                      <span className="text-[var(--loss)]">{chunks}</span>
                    ),
                    gainValue: config.checkCheckerGain * config.economyScale,
                    lossValue: config.checkVictimLoss * config.economyScale,
                  })}
                  {config.doubleCheckChipMultiplier !== 1 &&
                    t("doubleCheckClause", {
                      value: config.doubleCheckChipMultiplier,
                    })}
                  .
                </p>
              </div>
            </Card>

            {!isValid && (
              <Card padding="p-4" accentColorVar="--loss">
                <div className="text-sm text-[var(--loss)] font-semibold mb-1.5">
                  {t("fixBeforeStarting")}
                </div>
                <ul className="text-sm text-[var(--foreground)] list-disc pl-4 space-y-0.5">
                  {issues.slice(0, 6).map((issue, i) => (
                    <li key={i}>{issue.message}</li>
                  ))}
                </ul>
              </Card>
            )}

            {createError && (
              <AlreadyInGameNotice
                message={createError}
                className="text-sm text-[var(--loss)]"
              />
            )}

            {opponent === "someone" && !(challenge && !localExpired) && (
              <button
                type="button"
                onClick={handleCreateChallenge}
                disabled={!isValid || creating || loadingMine}
                className="btn-primary w-full py-2.5 text-sm"
              >
                {creating ? tChallenge("creating") : tChallenge("createLink")}
              </button>
            )}
            {opponent !== "someone" && (
              <button
                type="button"
                onClick={handleStart}
                disabled={!isValid || creating}
                className="btn-primary w-full py-2.5 text-sm"
              >
                {creating ? t("starting") : t("startGame")}
              </button>
            )}
          </aside>
        </div>
      </main>
      <Footer />
    </div>
  );
}

export default function CustomGamePage() {
  return (
    <RequireAuth>
      <CustomGameBuilder />
    </RequireAuth>
  );
}
