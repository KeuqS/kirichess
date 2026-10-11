import type { Metadata } from "next";
import { getLocale, getTranslations } from "next-intl/server";
import { NavBar } from "@/components/NavBar";
import { Footer } from "@/components/Footer";
import { routeMetadata } from "@/lib/seo";
import { GITHUB_URL } from "@/lib/site";

const API_BASE = process.env.NEXT_PUBLIC_API_BASE || "http://localhost:5053";

export async function generateMetadata(): Promise<Metadata> {
  const [locale, t] = await Promise.all([
    getLocale(),
    getTranslations("developers"),
  ]);
  // M-1-1 / M-1-2. The title and description were already translated here; what was missing is
  // everything around them, so this page inherited the root layout's whole openGraph object and
  // advertised itself as the site home to any crawler or chat unfurl.
  return routeMetadata({
    locale,
    path: "/developers",
    title: t("metaTitle"),
    description: t("metaDescription"),
  });
}

function hubUrl(apiBase: string): string {
  const url = new URL("/hubs/game", apiBase);
  url.protocol = url.protocol === "https:" ? "wss:" : "ws:";
  return url.toString();
}

function Code({ children }: { children: string }) {
  return (
    <pre className="overflow-x-auto border border-[var(--border)] bg-[var(--background)] p-4 text-sm leading-relaxed text-[var(--foreground)]">
      <code>{children}</code>
    </pre>
  );
}

export default async function Developers() {
  const t = await getTranslations("developers");
  const api = API_BASE.replace(/\/$/, "");
  const openApi = `${api}/openapi/v1.json`;
  const hub = hubUrl(api);

  return (
    <div className="min-h-screen flex flex-col bg-[var(--background)] text-[var(--foreground)] font-sans selection:bg-[var(--bounty)] selection:text-[#282c34]">
      <NavBar />

      <main
        id="main-content"
        className="flex-1 page-prose pt-10 pb-32 scroll-mt-24"
      >
        <section>
          <h1 className="text-3xl font-bold leading-tight tracking-tight mb-4 text-white text-balance">
            {t("heading")}
          </h1>
          <p className="text-[15px] text-[var(--foreground)] leading-relaxed mb-10">
            {t("subheading")}
          </p>

          <article className="mb-14">
            <h2 className="text-xl font-bold text-white mb-4 tracking-tight">
              {t("authHeading")}
            </h2>
            <div className="flex flex-col gap-5">
              <p className="text-[15px] text-[var(--foreground)] leading-relaxed">
                {t("authPara")}
              </p>
              <p className="text-[15px] text-[var(--foreground)] leading-relaxed">
                {t("tokenShape")}
              </p>
              <ul className="list-disc pl-5 space-y-2 text-[15px] text-[var(--foreground)] leading-relaxed">
                <li>{t("scopeRead")}</li>
                <li>{t("scopePlay")}</li>
              </ul>
              <p className="text-[15px] text-[var(--foreground)] leading-relaxed">
                {t("corsPara")}
              </p>
              <Code>{`curl ${api}/api/v1/account \\
  -H "Authorization: Bearer $KIRICHESS_API_TOKEN"`}</Code>
            </div>
          </article>

          <article className="mb-14">
            <h2 className="text-xl font-bold text-white mb-4 tracking-tight">
              {t("playHeading")}
            </h2>
            <div className="flex flex-col gap-5">
              <p className="text-[15px] text-[var(--foreground)] leading-relaxed">
                {t("playPara")}
              </p>
              <p className="text-[15px] text-[var(--foreground)] leading-relaxed">
                {t("poolBoundaryPara")}
              </p>
              <Code>{`curl -X POST ${api}/api/v1/games \\
  -H "Authorization: Bearer $KIRICHESS_API_TOKEN" \\
  -H "Content-Type: application/json" \\
  -d '{"preset":"bounty1","opponentId":null,"rated":false}'`}</Code>
            </div>
          </article>

          <article className="mb-14">
            <h2 className="text-xl font-bold text-white mb-4 tracking-tight">
              {t("hubHeading")}
            </h2>
            <div className="flex flex-col gap-5">
              <p className="text-[15px] text-[var(--foreground)] leading-relaxed">
                {t("hubPara", { hub })}
              </p>
              <p className="text-[15px] text-[var(--foreground)] leading-relaxed">
                {t("hubMethods")}
              </p>
            </div>
          </article>

          <article className="mb-14">
            <h2 className="text-xl font-bold text-white mb-4 tracking-tight">
              {t("limitsHeading")}
            </h2>
            <ul className="list-disc pl-5 space-y-2 text-[15px] text-[var(--foreground)] leading-relaxed">
              <li>{t("limitRead")}</li>
              <li>{t("limitCreate")}</li>
              <li>{t("limitMove")}</li>
              <li>{t("limitAccount")}</li>
            </ul>
          </article>

          <article className="mb-14">
            <h2 className="text-xl font-bold text-white mb-4 tracking-tight">
              {t("specHeading")}
            </h2>
            <p className="text-[15px] text-[var(--foreground)] leading-relaxed">
              {t("specPara")}{" "}
              <a
                href={openApi}
                className="text-[var(--bounty)] font-medium hover:underline"
              >
                {openApi}
              </a>
              .
            </p>
          </article>

          <article>
            <h2 className="text-xl font-bold text-white mb-4 tracking-tight">
              {t("sourceHeading")}
            </h2>
            <p className="text-[15px] text-[var(--foreground)] leading-relaxed">
              {t("sourcePara")}{" "}
              {GITHUB_URL ? (
                <a
                  href={GITHUB_URL}
                  className="text-[var(--bounty)] font-medium hover:underline"
                >
                  {t("sourceLinkLabel")}
                </a>
              ) : (
                t("sourcePlaceholder")
              )}
            </p>
          </article>
        </section>
      </main>
      <Footer />
    </div>
  );
}
