import { RouteLoading } from "@/components/RouteLoading";
import { BoardPageSkeleton } from "@/components/SkeletonRows";

export default function Loading() {
  return (
    <RouteLoading mainClassName="flex-1 page-board flex flex-col py-6 scroll-mt-24">
      <BoardPageSkeleton />
    </RouteLoading>
  );
}
