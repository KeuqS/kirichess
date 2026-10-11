import type { Metadata } from "next";

// See ui/app/learn/layout.tsx, same reason: the page below is a client component.
export const metadata: Metadata = {
  title: "Log in",
  description: "Log in or create a KiriChess account.",
};

export default function LoginLayout({
  children,
}: {
  children: React.ReactNode;
}) {
  return children;
}
