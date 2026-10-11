import type { Metadata } from "next";

// See ui/app/learn/layout.tsx, same reason: the page below is a client component.
export const metadata: Metadata = {
  title: "Forgot password",
  description: "Reset your KiriChess account password.",
};

export default function ForgotPasswordLayout({
  children,
}: {
  children: React.ReactNode;
}) {
  return children;
}
