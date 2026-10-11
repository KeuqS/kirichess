import { Card } from "@/components/Card";
import { RouteLoading } from "@/components/RouteLoading";
import { SkeletonRows } from "@/components/SkeletonRows";

export default function Loading() {
  return (
    <RouteLoading mainClassName="flex flex-1 items-center justify-center px-4 py-12 scroll-mt-24">
      <Card className="page-card">
        <SkeletonRows count={5} />
      </Card>
    </RouteLoading>
  );
}
