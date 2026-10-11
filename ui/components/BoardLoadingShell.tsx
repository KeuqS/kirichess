import { NavBar } from "@/components/NavBar";
import { BoardPageSkeleton } from "@/components/SkeletonRows";

/**
 * Shared loading shell for live play and spectate: nav, no footer, and the
 * board-page columns. The data wait and the auth gate both use it so the
 * layout does not jump on the way to the board.
 */
export function BoardLoadingShell({
  label,
  status,
}: {
  label: string;
  status?: string;
}) {
  const skeleton = <BoardPageSkeleton />;
  return (
    <div className="board-page-safe-bottom min-h-screen flex flex-col bg-[var(--background)] text-[var(--foreground)] font-sans selection:bg-[var(--bounty)] selection:text-[#282c34]">
      <NavBar />
      <main
        id="main-content"
        className="page-board flex flex-col py-6 scroll-mt-24"
      >
        <h1 className="sr-only">{label}</h1>
        {status ? (
          <div role="status">
            <span className="sr-only">{status}</span>
            {skeleton}
          </div>
        ) : (
          skeleton
        )}
      </main>
    </div>
  );
}
