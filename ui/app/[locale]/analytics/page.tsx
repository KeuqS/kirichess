"use client";

import { useEffect, useState } from "react";
import {
  IconAlertTriangle,
  IconChartLine,
  IconEye,
  IconScale,
} from "@tabler/icons-react";
import { Card } from "@/components/Card";
import { DataTable } from "@/components/DataTable";
import { TabChip } from "@/components/TabChip";
import { EmptyState } from "@/components/EmptyState";
import { NavBar } from "@/components/NavBar";
import { Footer } from "@/components/Footer";
import { RequireAuth } from "@/components/RequireAuth";
import { StaffOnly } from "@/components/ForbiddenScreen";
import { SkeletonRows } from "@/components/SkeletonRows";
import {
  ApiError,
  getBalanceAnalytics,
  getRetentionAnalytics,
  getTrafficAnalytics,
} from "@/lib/api";
import { PRESETS, isPresetKey } from "@/lib/presets";
import type {
  BalanceSummaryDto,
  CohortRowDto,
  FunnelStepDto,
  RetentionSummaryDto,
  TrafficSummaryDto,
} from "@/lib/types";

type Tab = "retention" | "traffic" | "balance";

const GRID_COLUMNS = "1.6fr 70px 80px 80px 100px 90px 90px 100px";

const WINDOWS: { label: string; days: number }[] = [
  { label: "30d", days: 30 },
  { label: "90d", days: 90 },
  { label: "1y", days: 365 },
  { label: "All", days: 3650 },
];

const TRAFFIC_WINDOWS: { label: string; days: number }[] = [
  { label: "7d", days: 7 },
  { label: "30d", days: 30 },
  { label: "90d", days: 90 },
];

const FUNNEL_COLORS = [
  "var(--cyan)",
  "var(--blue)",
  "var(--bounty)",
  "var(--gain)",
];

/** Fixed colour per termination reason, from CSS tokens only. Unmapped reasons fall back to `--border`. */
const TERMINATION_COLORS: Record<string, string> = {
  Checkmate: "var(--gain)",
  Flagfall: "var(--bounty)",
  Resignation: "var(--loss)",
  Abandoned: "var(--accent)",
  Stalemate: "var(--bounty)",
  DrawByRepetition: "var(--bounty)",
  DrawByInsufficientMaterial: "var(--bounty)",
  DrawByFiftyMove: "var(--blue)",
  DrawByAgreement: "var(--blue)",
};

const pct = (n: number) => `${n.toFixed(1)}%`;

/**
 * phase8a5 README scope decision 2: a null rate means the cohort has not had the chance yet, and a zero
 * means it had the chance and nobody came back. Rendering the first as "0%" is the single most
 * misleading thing this page could do - it makes every recent cohort look dead purely as a function of
 * when you loaded the page.
 */
function ratePct(value: number | null | undefined): string {
  return value === null || value === undefined ? "-" : `${value.toFixed(1)}%`;
}

/** DateOnly members serialise as "YYYY-MM-DD". Force UTC so a reader west of UTC does not see yesterday. */
function formatUtcDate(iso: string): string {
  const withTime = iso.includes("T") ? iso : `${iso}T00:00:00Z`;
  return new Date(withTime).toLocaleDateString(undefined, { timeZone: "UTC" });
}

/** 214.8 → "3m 35s"; 45 → "45s"; 5400 → "1h 30m". */
function formatDuration(seconds: number): string {
  const total = Math.round(seconds);
  const h = Math.floor(total / 3600);
  const m = Math.floor((total % 3600) / 60);
  const s = total % 60;
  if (h > 0) return `${h}h ${m}m`;
  if (m > 0) return `${m}m ${s}s`;
  return `${s}s`;
}

function poolLabel(pool: string): string {
  if (isPresetKey(pool)) return PRESETS[pool].label;
  if (pool === "custom") return "Custom / unrated";
  if (pool === "all") return "All pools";
  return pool.charAt(0).toUpperCase() + pool.slice(1);
}

/** Within ±2 of 50 → balanced, within ±5 → worth watching, beyond ±5 → favours a side. */
function whiteWinColorClass(value: number): string {
  const deviation = Math.abs(value - 50);
  if (deviation <= 2) return "text-[var(--gain)]";
  if (deviation <= 5) return "text-muted";
  return "text-[var(--loss)]";
}

function TabButton({
  active,
  onClick,
  icon,
  label,
}: {
  active: boolean;
  onClick: () => void;
  icon: React.ReactNode;
  label: string;
}) {
  return (
    <TabChip active={active} onClick={onClick} className="flex gap-2">
      {icon}
      {label}
    </TabChip>
  );
}

function WindowButtons({
  windows,
  sinceDays,
  onChange,
}: {
  windows: { label: string; days: number }[];
  sinceDays: number;
  onChange: (days: number) => void;
}) {
  return (
    <div className="flex gap-2 flex-wrap items-center">
      {windows.map((w) => (
        <TabChip
          key={w.days}
          active={w.days === sinceDays}
          onClick={() => onChange(w.days)}
        >
          {w.label}
        </TabChip>
      ))}
    </div>
  );
}

function TruncatedBanner() {
  return (
    <div className="mb-6 notice-banner flex items-start gap-2">
      <IconAlertTriangle size={16} stroke={2} className="mt-0.5 shrink-0" />
      <span>
        The row cap was hit: these numbers are the most recent sample, not the
        full history.
      </span>
    </div>
  );
}

function TabError({ error, status }: { error: string; status: number | null }) {
  if (status === 403) {
    return (
      <Card padding="py-12 px-8" className="shadow-lg">
        <EmptyState
          title="This area is for admins"
          description="You don't have access to the analytics dashboard."
        />
      </Card>
    );
  }
  return (
    <Card padding="py-12 px-8" className="shadow-lg">
      <EmptyState
        title="Couldn't load analytics"
        description={error}
      />
    </Card>
  );
}

function BarRow({
  label,
  children,
  value,
  valueMuted = false,
  valueTitle,
  detail,
  detailTitle,
}: {
  label: string;
  children: React.ReactNode;
  value?: React.ReactNode;
  valueMuted?: boolean;
  valueTitle?: string;
  detail?: string;
  detailTitle?: string;
}) {
  const hasFigure = value !== undefined || detail !== undefined;
  return (
    <div className="flex flex-col gap-1.5 sm:flex-row sm:items-center sm:gap-4">
      <div className="shrink-0 text-sm font-semibold text-white sm:w-48">
        {label}
      </div>
      <div className="flex min-w-0 flex-1 items-center gap-4">
        <div className="min-w-0 flex-1">{children}</div>
        {hasFigure ? (
          <div className="w-36 shrink-0 text-right text-sm tabular-nums">
            {value !== undefined ? (
              <span
                className={`font-semibold ${valueMuted ? "text-muted" : "text-white"}`}
                title={valueTitle}
              >
                {value}
              </span>
            ) : null}
            {detail !== undefined ? (
              <span className="text-muted ml-2" title={detailTitle}>
                {detail}
              </span>
            ) : null}
          </div>
        ) : null}
      </div>
    </div>
  );
}

function MeterTrack({ children }: { children?: React.ReactNode }) {
  return (
    <div className="flex h-4 overflow-hidden bg-surface-sunken">{children}</div>
  );
}

function MeterFill({ percent, color }: { percent: number; color: string }) {
  const width = Number.isFinite(percent)
    ? Math.min(100, Math.max(0, percent))
    : 0;
  return (
    <div
      className="h-full"
      style={{ width: `${width}%`, backgroundColor: color }}
    />
  );
}

function FunnelBars({
  steps,
  rateTitle,
}: {
  steps: FunnelStepDto[];
  rateTitle: string;
}) {
  return (
    <div className="flex flex-col gap-3">
      {steps.map((step, i) => {
        const percent = step.percentOfSignups;
        const missing = percent === null || percent === undefined;
        return (
          <BarRow
            key={step.step}
            label={step.label}
            value={step.count}
            detail={ratePct(percent)}
            detailTitle={missing ? rateTitle : undefined}
          >
            <MeterTrack>
              <MeterFill
                percent={missing ? 0 : percent}
                color={FUNNEL_COLORS[i % FUNNEL_COLORS.length]}
              />
            </MeterTrack>
          </BarRow>
        );
      })}
    </div>
  );
}

function RetentionTab() {
  const [sinceDays, setSinceDays] = useState(90);
  const [grain, setGrain] = useState<"day" | "week">("week");
  const [summary, setSummary] = useState<RetentionSummaryDto | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [errorStatus, setErrorStatus] = useState<number | null>(null);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    setError(null);
    setErrorStatus(null);
    getRetentionAnalytics({ sinceDays, grain })
      .then((data) => {
        if (!cancelled) setSummary(data);
      })
      .catch((err) => {
        if (cancelled) return;
        setErrorStatus(err instanceof ApiError ? err.status : null);
        setError(
          err instanceof ApiError ? err.message : "Failed to load analytics.",
        );
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, [sinceDays, grain]);

  const cohortGrid = summary
    ? `1.4fr 70px 90px 90px ${summary.retention.map(() => "90px").join(" ")}`
    : "1fr";

  return (
    <>
      <p className="text-sm text-muted mb-6 leading-relaxed max-w-3xl">
        Retention is rolling: &quot;day 7&quot; means the account played again a
        week or more after signing up, not that it played on the seventh day
        exactly.
      </p>

      <div className="flex gap-2 mb-8 flex-wrap items-center">
        <WindowButtons
          windows={WINDOWS}
          sinceDays={sinceDays}
          onChange={setSinceDays}
        />
        <span className="w-px h-6 bg-[var(--border)] mx-1" />
        {(["week", "day"] as const).map((g) => (
          <TabChip key={g} active={g === grain} onClick={() => setGrain(g)}>
            {g === "week" ? "By week" : "By day"}
          </TabChip>
        ))}
      </div>

      {summary?.truncated && <TruncatedBanner />}

      {loading ? (
        <Card padding="p-5" className="shadow-lg">
          <SkeletonRows count={6} />
        </Card>
      ) : error ? (
        <TabError error={error} status={errorStatus} />
      ) : !summary || summary.totalSignups === 0 ? (
        <Card padding="py-12 px-8" className="shadow-lg">
          <EmptyState
            title="No signups in this window"
            description="Widen the window or wait for the first accounts, then check back."
          />
        </Card>
      ) : (
        <div className="flex flex-col gap-14">
          <section>
            <h2 className="profile-category">Acquisition funnel</h2>
            <FunnelBars
              steps={summary.funnel}
              rateTitle="Not enough time has passed for this cohort yet"
            />
          </section>

          <section>
            <h2 className="profile-category">Retention curve</h2>
            <div className="flex flex-col gap-3">
              {summary.retention.map((point) => {
                const percent = point.percent;
                const missing = percent === null || percent === undefined;
                return (
                  <BarRow
                    key={point.day}
                    label={`D${point.day}`}
                    value={ratePct(percent)}
                    valueMuted={missing}
                    valueTitle={
                      missing
                        ? "Not enough time has passed for this cohort yet"
                        : undefined
                    }
                    detail={`${point.retained} / ${point.eligible}`}
                  >
                    <MeterTrack>
                      <MeterFill
                        percent={missing ? 0 : percent}
                        color="var(--cyan)"
                      />
                    </MeterTrack>
                  </BarRow>
                );
              })}
            </div>
          </section>

          <section>
            <h2 className="profile-category">Cohorts</h2>
          <DataTable
            gridTemplateColumns={cohortGrid}
            rows={summary.cohorts}
            rowKey={(row: CohortRowDto) => row.cohortStart}
            columns={[
              {
                header: "COHORT",
                render: (row) => (
                  <span className="font-semibold text-white">
                    {formatUtcDate(row.cohortStart)}
                  </span>
                ),
              },
              {
                header: "SIGNUPS",
                align: "right",
                render: (row) => <span className="text-sm">{row.signups}</span>,
              },
              {
                header: "1ST GAME",
                align: "right",
                render: (row) => (
                  <span className="text-sm">{row.playedFirstGame}</span>
                ),
              },
              {
                header: "2ND GAME",
                align: "right",
                render: (row) => (
                  <span className="text-sm">{row.playedSecondGame}</span>
                ),
              },
              ...summary.retention.map((horizon, i) => ({
                header: `D${horizon.day}`,
                align: "right" as const,
                render: (row: CohortRowDto) => {
                  const point = row.retention[i];
                  const empty =
                    !point ||
                    point.percent === null ||
                    point.percent === undefined;
                  return (
                    <span
                      className={`text-sm ${empty ? "text-muted" : ""}`}
                      title={
                        empty
                          ? "Not enough time has passed for this cohort yet"
                          : undefined
                      }
                    >
                      {point ? ratePct(point.percent) : "-"}
                    </span>
                  );
                },
              })),
            ]}
          />
          </section>
        </div>
      )}
    </>
  );
}

function CountBar({
  label,
  count,
  max,
  color,
  sub,
  subTitle,
}: {
  label: string;
  count: number;
  max: number;
  color: string;
  sub?: string;
  subTitle?: string;
}) {
  const width = max <= 0 ? 0 : (count / max) * 100;
  return (
    <BarRow label={label} value={count} detail={sub} detailTitle={subTitle}>
      <MeterTrack>
        <MeterFill percent={width} color={color} />
      </MeterTrack>
    </BarRow>
  );
}

function TrafficTab() {
  const [sinceDays, setSinceDays] = useState(30);
  const [summary, setSummary] = useState<TrafficSummaryDto | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [errorStatus, setErrorStatus] = useState<number | null>(null);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    setError(null);
    setErrorStatus(null);
    getTrafficAnalytics({ sinceDays })
      .then((data) => {
        if (!cancelled) setSummary(data);
      })
      .catch((err) => {
        if (cancelled) return;
        setErrorStatus(err instanceof ApiError ? err.status : null);
        setError(
          err instanceof ApiError ? err.message : "Failed to load analytics.",
        );
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, [sinceDays]);

  const sourceSignupSum =
    summary?.bySource.reduce((n, row) => n + row.signups, 0) ?? 0;
  const fs = summary?.firstSession;
  const funnelMax = fs
    ? Math.max(
        fs.learnStarted,
        fs.learnCompleted,
        fs.queueJoined,
        fs.queueMatched,
        fs.gamesStarted,
        fs.gamesFinished,
        1,
      )
    : 1;
  const maxPageViews = summary
    ? Math.max(1, ...summary.byPage.map((p) => p.count), 1)
    : 1;
  const maxSourceViews = summary
    ? Math.max(1, ...summary.bySource.map((s) => s.pageViews), 1)
    : 1;
  const maxDayViews = summary
    ? Math.max(1, ...summary.days.map((d) => d.pageViews), 1)
    : 1;
  const maxDaySignups = summary
    ? Math.max(1, ...summary.days.map((d) => d.signups), 1)
    : 1;

  return (
    <>
      <div className="mb-8">
        <WindowButtons
          windows={TRAFFIC_WINDOWS}
          sinceDays={sinceDays}
          onChange={setSinceDays}
        />
      </div>

      {summary?.truncated && <TruncatedBanner />}

      {loading ? (
        <Card padding="p-5" className="shadow-lg">
          <SkeletonRows count={6} />
        </Card>
      ) : error ? (
        <TabError error={error} status={errorStatus} />
      ) : !summary ? (
        <Card padding="py-12 px-8" className="shadow-lg">
          <EmptyState
            title="No traffic in this window"
            description="Widen the window or wait for visitors, then check back."
          />
        </Card>
      ) : (
        <div className="flex flex-col gap-14">
          <div>
          <div className="grid grid-cols-1 md:grid-cols-3 gap-6">
            <Card padding="p-5">
              <div className="text-sm tracking-widest text-muted font-semibold mb-2">
                PAGE VIEWS
              </div>
              <div className="text-3xl font-bold text-white">
                {summary.totalPageViews}
              </div>
            </Card>
            <Card padding="p-5">
              <div className="text-sm tracking-widest text-muted font-semibold mb-2">
                SIGNUPS
              </div>
              <div className="text-3xl font-bold text-white">
                {summary.totalSignups}
              </div>
            </Card>
            <Card padding="p-5">
              <div className="text-sm tracking-widest text-muted font-semibold mb-2">
                SIGNUPS PER 100 VIEWS
              </div>
              <div
                className={`text-3xl font-bold ${
                  summary.signupsPerHundredLandingViews === null ||
                  summary.signupsPerHundredLandingViews === undefined
                    ? "text-muted"
                    : "text-white"
                }`}
                title={
                  summary.signupsPerHundredLandingViews === null ||
                  summary.signupsPerHundredLandingViews === undefined
                    ? "No landing-page views in this window yet"
                    : undefined
                }
              >
                {ratePct(summary.signupsPerHundredLandingViews)}
              </div>
              <div className="text-sm text-muted mt-1">
                of landing views, not a conversion rate
              </div>
            </Card>
          </div>
          <p className="text-sm text-muted mt-5 leading-relaxed max-w-3xl">
            These are events, not people. Nothing here identifies a visitor, no
            cookie, no session, no IP, so one person refreshing five times
            counts five times, and crawlers and load tests count too. Use it to
            spot a change in scale, not as a headcount.
          </p>
          </div>

          <section>
          <h2 className="profile-category">First session</h2>
          <div className="flex flex-col gap-3">
            <CountBar
              label="Started the tutorial"
              count={fs!.learnStarted}
              max={funnelMax}
              color="var(--cyan)"
            />
            <CountBar
              label="Finished the tutorial"
              count={fs!.learnCompleted}
              max={funnelMax}
              color="var(--blue)"
              sub={`${ratePct(fs!.learnCompletionPercent)} of starts`}
              subTitle={
                fs!.learnCompletionPercent === null ||
                fs!.learnCompletionPercent === undefined
                  ? "No tutorial starts in this window yet"
                  : undefined
              }
            />
            <BarRow label="Skipped the tutorial" value={fs!.learnSkipped}>
              <p className="text-sm text-muted leading-relaxed">
                an exit, not a step
              </p>
            </BarRow>
            <CountBar
              label="Looked for an opponent"
              count={fs!.queueJoined}
              max={funnelMax}
              color="var(--bounty)"
            />
            <CountBar
              label="Found one"
              count={fs!.queueMatched}
              max={funnelMax}
              color="var(--gain)"
              sub={`${ratePct(fs!.queueMatchPercent)} of searches`}
              subTitle={
                fs!.queueMatchPercent === null ||
                fs!.queueMatchPercent === undefined
                  ? "No queue searches in this window yet"
                  : undefined
              }
            />
            <CountBar
              label="Games started"
              count={fs!.gamesStarted}
              max={funnelMax}
              color="var(--cyan)"
            />
            <CountBar
              label="Games finished"
              count={fs!.gamesFinished}
              max={funnelMax}
              color="var(--blue)"
              sub={`${ratePct(fs!.gameFinishPercent)} of starts`}
              subTitle={
                fs!.gameFinishPercent === null ||
                fs!.gameFinishPercent === undefined
                  ? "No games started in this window yet"
                  : undefined
              }
            />
          </div>
          <div className="mt-5 flex flex-col gap-5 text-sm text-muted leading-relaxed max-w-3xl">
            <p>
              &quot;Looked for an opponent&quot; and &quot;Found one&quot; are
              counted per person, so the gap between them is people who waited
              and gave up, an empty lobby. There is no separate &quot;gave
              up&quot; number because it is exactly that difference.
            </p>
            <p>
              Games are counted from the games themselves, not from a beacon, so
              they are exact.
            </p>
            <p>
              &quot;Games finished&quot; can exceed &quot;games started&quot;
              when a game began before the window and ended inside it. The
              percentage is not clamped.
            </p>
          </div>
          </section>

          <section>
          <h2 className="profile-category">By source</h2>
          <div className="flex flex-col gap-3">
            {summary.bySource.map((row) => (
              <BarRow
                key={row.source}
                label={row.source === "direct" ? "Direct" : row.source}
                value={row.pageViews}
                detail={`${row.signups} signups`}
              >
                <MeterTrack>
                  <MeterFill
                    percent={(row.pageViews / maxSourceViews) * 100}
                    color="var(--cyan)"
                  />
                </MeterTrack>
              </BarRow>
            ))}
          </div>
          <div className="mt-5 flex flex-col gap-5 max-w-3xl">
            <p className="text-sm text-muted leading-relaxed">
              A source comes from a{" "}
              <code className="font-mono text-[var(--bounty)]">?src=</code> in
              the link that was clicked and is remembered only for the current
              tab. The signup numbers here are counted in the browser and can
              be lower than the total above, which is counted in the database.
              The database is the one to trust.
            </p>
            {sourceSignupSum < summary.totalSignups ? (
              <p className="text-sm text-muted leading-relaxed">
                Browser-attributed signups sum to {sourceSignupSum}; the
                database counted {summary.totalSignups}. Trust the database.
              </p>
            ) : null}
          </div>
          </section>

          <section>
          <h2 className="profile-category">Pages</h2>
          <div className="flex flex-col gap-3">
            {summary.byPage.map((row) => (
              <BarRow key={row.key} label={row.key} value={row.count}>
                <MeterTrack>
                  <MeterFill
                    percent={(row.count / maxPageViews) * 100}
                    color="var(--blue)"
                  />
                </MeterTrack>
              </BarRow>
            ))}
          </div>
          </section>

          <section>
          <h2 className="profile-category">Days</h2>
          {summary.days.length === 0 ? (
            <p className="text-sm text-muted">No days in this window.</p>
          ) : (
            <div className="flex flex-col gap-3">
              <div className="flex flex-wrap gap-x-4 gap-y-2">
                <div className="flex items-center gap-1.5 text-sm text-muted">
                  <span className="inline-block h-2.5 w-2.5 bg-[var(--cyan)]" />
                  Page views
                </div>
                <div className="flex items-center gap-1.5 text-sm text-muted">
                  <span className="inline-block h-2.5 w-2.5 bg-[var(--gain)]" />
                  Signups
                </div>
              </div>
              <div className="flex items-end gap-0.5 h-16">
                {summary.days.map((d) => (
                  <div
                    key={`v-${d.date}`}
                    className="flex-1 bg-[var(--cyan)] min-w-[2px]"
                    style={{
                      height: `${(d.pageViews / maxDayViews) * 100}%`,
                    }}
                    title={`${formatUtcDate(d.date)}: ${d.pageViews} views`}
                  />
                ))}
              </div>
              <div className="flex items-end gap-0.5 h-10">
                {summary.days.map((d) => (
                  <div
                    key={`s-${d.date}`}
                    className="flex-1 bg-[var(--gain)] min-w-[2px]"
                    style={{
                      height: `${(d.signups / maxDaySignups) * 100}%`,
                    }}
                    title={`${formatUtcDate(d.date)}: ${d.signups} signups`}
                  />
                ))}
              </div>
              <div className="flex justify-between text-sm text-muted">
                <span>{formatUtcDate(summary.days[0].date)}</span>
                <span>
                  {formatUtcDate(summary.days[summary.days.length - 1].date)}
                </span>
              </div>
            </div>
          )}
          </section>
        </div>
      )}
    </>
  );
}

function BalanceTab() {
  const [sinceDays, setSinceDays] = useState(90);
  const [ratedOnly, setRatedOnly] = useState(false);
  const [summary, setSummary] = useState<BalanceSummaryDto | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [errorStatus, setErrorStatus] = useState<number | null>(null);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    setError(null);
    setErrorStatus(null);
    getBalanceAnalytics({ sinceDays, ratedOnly })
      .then((data) => {
        if (!cancelled) setSummary(data);
      })
      .catch((err) => {
        if (cancelled) return;
        setErrorStatus(err instanceof ApiError ? err.status : null);
        setError(
          err instanceof ApiError ? err.message : "Failed to load analytics.",
        );
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, [sinceDays, ratedOnly]);

  const overall = summary?.overall ?? null;
  const noGames = summary !== null && summary.totalGames === 0;
  const legendReasons = summary
    ? Array.from(
        new Set(
          summary.pools.flatMap((p) => p.terminations.map((t) => t.reason)),
        ),
      )
    : [];

  return (
    <>
      <div className="flex gap-2 mb-8 flex-wrap items-center">
        <WindowButtons
          windows={WINDOWS}
          sinceDays={sinceDays}
          onChange={setSinceDays}
        />
        <span className="w-px h-6 bg-[var(--border)] mx-1" />
        <TabChip active={ratedOnly} onClick={() => setRatedOnly((v) => !v)}>
          Rated only
        </TabChip>
      </div>

      {summary?.truncated && <TruncatedBanner />}

      {loading ? (
        <Card padding="p-5" className="shadow-lg">
          <SkeletonRows count={6} />
        </Card>
      ) : error ? (
        <TabError error={error} status={errorStatus} />
      ) : !summary || !overall || noGames ? (
        <Card padding="py-12 px-8" className="shadow-lg">
          <EmptyState
            title="No finished games in this window"
            description="Widen the window or wait for more games to finish, then check back."
          />
        </Card>
      ) : (
        <div className="flex flex-col gap-14">
          <div>
          <div className="grid grid-cols-2 md:grid-cols-4 gap-6">
            <Card padding="p-5">
              <div className="text-sm tracking-widest text-muted font-semibold mb-2">
                GAMES
              </div>
              <div className="text-3xl font-bold text-white">
                {summary.totalGames}
              </div>
              <div className="text-sm text-muted mt-1">
                {summary.excludedGames} excluded
              </div>
            </Card>
            <Card padding="p-5">
              <div className="text-sm tracking-widest text-muted font-semibold mb-2">
                WHITE WIN RATE
              </div>
              <div
                className={`text-3xl font-bold ${whiteWinColorClass(overall.whiteWinPercent)}`}
              >
                {pct(overall.whiteWinPercent)}
              </div>
            </Card>
            <Card padding="p-5">
              <div className="text-sm tracking-widest text-muted font-semibold mb-2">
                CHECKMATE
              </div>
              <div className="text-3xl font-bold text-white">
                {pct(overall.checkmatePercent)}
              </div>
              <div className="text-sm text-muted mt-1">
                {pct(overall.flagfallPercent)} flagfall
              </div>
            </Card>
            <Card padding="p-5">
              <div className="text-sm tracking-widest text-muted font-semibold mb-2">
                AVG LENGTH
              </div>
              <div className="text-3xl font-bold text-white">
                {overall.avgPlies} plies
              </div>
              <div className="text-sm text-muted mt-1">
                {formatDuration(overall.avgDurationSeconds)}
              </div>
            </Card>
          </div>
          <p className="text-sm text-muted mt-5 leading-relaxed">
            White win rate is coloured by deviation from 50%:{" "}
            <span className="text-[var(--gain)] font-semibold">within ±2</span>{" "}
            is balanced,{" "}
            <span className="text-muted font-semibold">within ±5</span> is worth
            watching,{" "}
            <span className="text-[var(--loss)] font-semibold">beyond ±5</span>{" "}
            means the variant favours a side.
          </p>
          </div>

          <DataTable
            gridTemplateColumns={GRID_COLUMNS}
            rows={summary.pools}
            rowKey={(row) => row.pool}
            columns={[
              {
                header: "POOL",
                render: (row) => (
                  <span className="font-semibold text-white">
                    {poolLabel(row.pool)}
                  </span>
                ),
              },
              {
                header: "GAMES",
                align: "right",
                render: (row) => (
                  <span className="text-sm text-muted">{row.games}</span>
                ),
              },
              {
                header: "WHITE %",
                align: "right",
                render: (row) => (
                  <span
                    className={`text-sm font-semibold ${whiteWinColorClass(row.whiteWinPercent)}`}
                  >
                    {pct(row.whiteWinPercent)}
                  </span>
                ),
              },
              {
                header: "DRAW %",
                align: "right",
                render: (row) => (
                  <span className="text-sm text-muted">
                    {pct(row.drawPercent)}
                  </span>
                ),
              },
              {
                header: "CHECKMATE %",
                align: "right",
                render: (row) => (
                  <span className="text-sm text-[var(--gain)] font-semibold">
                    {pct(row.checkmatePercent)}
                  </span>
                ),
              },
              {
                header: "FLAGFALL %",
                align: "right",
                render: (row) => (
                  <span className="text-sm text-[var(--bounty)] font-semibold">
                    {pct(row.flagfallPercent)}
                  </span>
                ),
              },
              {
                header: "AVG PLIES",
                align: "right",
                render: (row) => (
                  <span className="text-sm text-muted">{row.avgPlies}</span>
                ),
              },
              {
                header: "AVG LENGTH",
                align: "right",
                render: (row) => (
                  <span className="text-sm text-muted">
                    {formatDuration(row.avgDurationSeconds)}
                  </span>
                ),
              },
            ]}
          />

          <section>
            <h2 className="profile-category">Termination mix</h2>
            <div className="flex flex-col gap-3">
              {summary.pools.map((poolRow) => (
                <BarRow key={poolRow.pool} label={poolLabel(poolRow.pool)}>
                  <MeterTrack>
                    {poolRow.terminations.map((slice) => (
                      <div
                        key={slice.reason}
                        className="h-full"
                        style={{
                          width: `${slice.percent}%`,
                          backgroundColor:
                            TERMINATION_COLORS[slice.reason] ?? "var(--border)",
                        }}
                        title={`${slice.reason}: ${pct(slice.percent)} (${slice.count})`}
                      />
                    ))}
                  </MeterTrack>
                </BarRow>
              ))}
            </div>
            <div className="flex flex-wrap gap-x-4 gap-y-2 mt-5">
              {legendReasons.map((reason) => (
                <div
                  key={reason}
                  className="flex items-center gap-1.5 text-sm text-muted"
                >
                  <span
                    className="inline-block h-2.5 w-2.5"
                    style={{
                      backgroundColor:
                        TERMINATION_COLORS[reason] ?? "var(--border)",
                    }}
                  />
                  {reason}
                </div>
              ))}
            </div>
          </section>
        </div>
      )}
    </>
  );
}

export default function AnalyticsPage() {
  const [tab, setTab] = useState<Tab>("retention");

  return (
    <div className="min-h-screen flex flex-col bg-[var(--background)] text-[var(--foreground)] font-sans selection:bg-[var(--bounty)] selection:text-[#282c34]">
      <NavBar />
      {/* isAdmin only hides page content; the server enforces this on every route regardless. */}
      <StaffOnly>
        <RequireAuth>
          <main
            id="main-content"
            className="flex-1 page-width pt-10 pb-24 scroll-mt-24"
          >
            <h1 className="text-3xl font-bold leading-tight tracking-tight mb-4 text-white text-balance">
              Analytics
            </h1>
            <p className="text-sm text-muted mb-8">
              Retention, first-session traffic, and the balance tuning
              instrument.
            </p>

            <div className="flex gap-2 mb-8 flex-wrap">
              <TabButton
                active={tab === "retention"}
                onClick={() => setTab("retention")}
                icon={<IconChartLine size={16} stroke={2} />}
                label="Retention"
              />
              <TabButton
                active={tab === "traffic"}
                onClick={() => setTab("traffic")}
                icon={<IconEye size={16} stroke={2} />}
                label="Traffic"
              />
              <TabButton
                active={tab === "balance"}
                onClick={() => setTab("balance")}
                icon={<IconScale size={16} stroke={2} />}
                label="Balance"
              />
            </div>

            {tab === "retention" && <RetentionTab />}
            {tab === "traffic" && <TrafficTab />}
            {tab === "balance" && <BalanceTab />}
          </main>
        </RequireAuth>
      </StaffOnly>
      <Footer />
    </div>
  );
}
