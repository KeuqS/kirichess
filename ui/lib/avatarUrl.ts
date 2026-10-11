const API_BASE =
  process.env.NEXT_PUBLIC_API_BASE ?? "http://localhost:5053";

/** Turns a same-API avatar path into an absolute URL for `<img src>`. */
export function resolveAvatarUrl(path: string | null | undefined): string | null {
  if (!path) return null;
  if (
    path.startsWith("http://") ||
    path.startsWith("https://") ||
    path.startsWith("data:") ||
    path.startsWith("blob:")
  ) {
    return path;
  }
  return `${API_BASE}${path.startsWith("/") ? path : `/${path}`}`;
}
