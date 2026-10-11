"use client";

import { useTranslations } from "next-intl";
import { Card } from "@/components/Card";
import type { PieceChar } from "@/lib/fen";

const GLYPH: Record<PieceChar, string> = {
  p: "♙",
  n: "♘",
  b: "♗",
  r: "♖",
  q: "♕",
  k: "♔",
};

function CapturedRow({
  label,
  pieces,
  seconds,
}: {
  label: string;
  pieces: PieceChar[];
  seconds: number;
}) {
  const rounded = Math.round(seconds);
  return (
    <div className="flex flex-col gap-1">
      <span className="text-sm text-muted">{label}</span>
      <div className="flex items-center justify-between">
        <span className="min-h-[26px] text-xl tracking-widest text-white">
          {pieces.map((p) => GLYPH[p]).join("")}
        </span>
        <span className="text-lg font-bold text-white">
          {rounded >= 0 ? "+" : ""}
          {rounded}s
        </span>
      </div>
    </div>
  );
}

interface CapturedMaterialPanelProps {
  opponentLabel: string;
  opponentPieces: PieceChar[];
  opponentSeconds: number;
  yourPieces: PieceChar[];
  yourSeconds: number;
}

export function CapturedMaterialPanel({
  opponentLabel,
  opponentPieces,
  opponentSeconds,
  yourPieces,
  yourSeconds,
}: CapturedMaterialPanelProps) {
  const t = useTranslations("CapturedMaterialPanel");
  return (
    <Card>
      <div className="mb-3 text-sm font-semibold tracking-widest text-muted">
        {t("heading")}
      </div>
      <CapturedRow
        label={t("took", { label: opponentLabel })}
        pieces={opponentPieces}
        seconds={opponentSeconds}
      />
      <div className="my-3 h-[1px] bg-[var(--border)]"></div>
      <CapturedRow
        label={t("youTook")}
        pieces={yourPieces}
        seconds={yourSeconds}
      />
    </Card>
  );
}
